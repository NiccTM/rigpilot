<#
.SYNOPSIS
    Safe replacement of a published RigPilot payload directory.

.DESCRIPTION
    Publishing used to clear the destination and then build into it. That ordering
    destroys the only known-good payload before a replacement exists, and it fails
    badly in the ordinary case where the dashboard is running out of the very
    directory being replaced: Remove-Item deletes part of the tree, hits the locked
    file, and aborts - leaving a half-deleted payload that later `-SkipPublish`
    installer builds cannot use.

    This dot-sourced helper inverts the order. Nothing in the destination is touched
    until a complete replacement has been built and validated in a staging directory
    beside it, and the destination's replaceability is proven up front so an
    unreplaceable destination fails before any work starts rather than after it has
    been damaged.

    Promotion itself is rename-based: the existing payload is renamed aside, the
    staging directory is renamed into place, and only then is the old copy removed.
    A failure part-way through renames the old payload back.

.NOTES
    Dot-source this file; it defines functions and performs no work on load.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Walks a payload tree without ever following a reparse point.
.DESCRIPTION
    Every traversal in this file goes through here rather than through
    Get-ChildItem -Recurse, which descends into junctions and directory symbolic
    links. That behaviour is the difference between deleting a publication
    directory and deleting whatever a junction inside it happens to point at, so
    the recursion is explicit and checks FileAttributes.ReparsePoint before it
    descends.

    Reparse points are reported, never entered. A tree containing one is refused
    outright by Assert-PayloadFreeOfReparsePoint - a published payload has no
    legitimate reason to contain a link, and honouring one during hashing,
    probing or cleanup would take the operation outside the directory the
    operator named.
.OUTPUTS
    An object with Files (paths of real files) and ReparsePoints (paths of links,
    not descended into).
#>
function Read-PayloadTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Root
    )

    $files = New-Object System.Collections.Generic.List[string]
    $reparsePoints = New-Object System.Collections.Generic.List[string]
    $full = [System.IO.Path]::GetFullPath($Root)
    if (-not [System.IO.Directory]::Exists($full)) {
        return [pscustomobject]@{ Files = @(); ReparsePoints = @() }
    }

    # The root itself can be a junction. Entering it would take everything below
    # out of the operator's directory.
    if (([System.IO.File]::GetAttributes($full) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        $reparsePoints.Add($full)
        return [pscustomobject]@{ Files = @(); ReparsePoints = @($reparsePoints) }
    }

    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($full)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($current)) {
            $attributes = [System.IO.File]::GetAttributes($entry)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $reparsePoints.Add($entry)
                continue
            }
            if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
            else {
                $files.Add($entry)
            }
        }
    }

    return [pscustomobject]@{ Files = @($files); ReparsePoints = @($reparsePoints) }
}

<#
.SYNOPSIS
    Throws when a payload tree contains a junction, symbolic link or other
    reparse point.
#>
function Assert-PayloadFreeOfReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Root,

        [string]$Stage = "in the payload"
    )

    $found = @((Read-PayloadTree -Root $Root).ReparsePoints)
    if ($found.Count -eq 0) {
        return
    }

    $listed = ($found | Select-Object -First 10) -join [Environment]::NewLine
    $more = if ($found.Count -gt 10) { "$([Environment]::NewLine)... and $($found.Count - 10) more." } else { "" }
    throw @"
Publication refused: $($found.Count) reparse point(s) (junction, symbolic link, or mount point) were found $Stage under $Root. Nothing has been modified.
A published payload must contain no links. Following one during replacement or cleanup would read or delete files outside the publication directory.
Remove the link(s) below, or publish to a different -OutputDirectory.
$listed$more
"@
}

<#
.SYNOPSIS
    Returns the files under a payload root that cannot currently be replaced.
.DESCRIPTION
    A file another process holds open cannot be deleted or renamed over. Opening
    with FileShare.None is the direct test for that: it succeeds only when no other
    handle exists. UnauthorizedAccessException is reported too - a read-only or
    ACL-blocked file is equally unreplaceable, and calling it "not locked" would be
    the same lie by a different route.
.OUTPUTS
    An array of full paths. Empty when the destination can be replaced.
#>
function Get-UnreplaceablePayloadFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Root
    )

    $blocking = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }

    foreach ($file in (Read-PayloadTree -Root $Root).Files) {
        try {
            $stream = [System.IO.File]::Open(
                $file,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            $stream.Dispose()
        }
        catch [System.IO.IOException] {
            $blocking.Add($file)
        }
        catch [System.UnauthorizedAccessException] {
            $blocking.Add($file)
        }
    }

    return @($blocking)
}

<#
.SYNOPSIS
    Deletes a tree, removing any reparse point as a link and never as its target.
.DESCRIPTION
    Remove-Item -Recurse has historically followed junctions and deleted their
    contents. This walks the tree itself: a reparse point is unlinked with the
    non-recursive Directory.Delete (or File.Delete), which detaches the link and
    leaves whatever it pointed at alone.
#>
function Remove-PayloadTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Path
    )

    $full = [System.IO.Path]::GetFullPath($Path)
    $isDirectory = [System.IO.Directory]::Exists($full)
    if (-not $isDirectory -and -not [System.IO.File]::Exists($full)) {
        return
    }

    $attributes = [System.IO.File]::GetAttributes($full)
    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        # Unlink only. The recursive overload would delete through the link.
        if ($isDirectory) {
            [System.IO.Directory]::Delete($full, $false)
        }
        else {
            [System.IO.File]::Delete($full)
        }
        return
    }

    if (-not $isDirectory) {
        if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
            [System.IO.File]::SetAttributes($full, $attributes -bxor [System.IO.FileAttributes]::ReadOnly)
        }
        [System.IO.File]::Delete($full)
        return
    }

    foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($full)) {
        Remove-PayloadTree -Path $entry
    }
    [System.IO.Directory]::Delete($full, $false)
}

<#
.SYNOPSIS
    Throws unless every file in the destination can be replaced.
#>
function Assert-PayloadDestinationReplaceable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DestinationRoot,

        [string]$Stage = "before publishing"
    )

    # A link is checked for first: a destination containing one cannot be probed
    # or cleaned up safely, so it is refused rather than assessed.
    Assert-PayloadFreeOfReparsePoint -Root $DestinationRoot -Stage "in the existing payload ($Stage)"

    $blocking = @(Get-UnreplaceablePayloadFile -Root $DestinationRoot)
    if ($blocking.Count -eq 0) {
        return
    }

    # Name the files. "Access denied" without the path is what made the original
    # failure hard to act on, and the fix is almost always "close the dashboard".
    $listed = ($blocking | Select-Object -First 10) -join [Environment]::NewLine
    $more = if ($blocking.Count -gt 10) { "$([Environment]::NewLine)... and $($blocking.Count - 10) more." } else { "" }
    throw @"
The existing payload at $DestinationRoot cannot be replaced $Stage because $($blocking.Count) file(s) are locked or read-only. The previous payload has NOT been modified.
Close any RigPilot process running from this directory (dashboard, service, hosts, CLI) and publish again, or publish to a different -OutputDirectory.
$listed$more
"@
}

<#
.SYNOPSIS
    Creates a unique staging directory beside the destination.
.DESCRIPTION
    A sibling keeps staging on the same volume, so promotion is a rename rather
    than a copy - that is what makes the swap fast and near-atomic.
#>
function New-PayloadStagingDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DestinationRoot
    )

    $parent = Split-Path -Parent ([System.IO.Path]::GetFullPath($DestinationRoot))
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "The payload destination has no parent directory: $DestinationRoot"
    }

    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $leaf = Split-Path -Leaf ([System.IO.Path]::GetFullPath($DestinationRoot))
    $staging = Join-Path $parent (".{0}-staging-{1}" -f $leaf, [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    return [System.IO.Path]::GetFullPath($staging)
}

<#
.SYNOPSIS
    Removes an incomplete staging directory. Never touches the destination.
#>
function Remove-PayloadStagingDirectory {
    [CmdletBinding()]
    param([string]$StagingRoot)

    if ([string]::IsNullOrWhiteSpace($StagingRoot) -or -not (Test-Path -LiteralPath $StagingRoot)) {
        return
    }

    try {
        Remove-PayloadTree -Path $StagingRoot
    }
    catch {
        # Staging is disposable, but a leftover directory is still worth naming:
        # a silent leak here is how a half-written payload gets mistaken for a
        # real one later.
        Write-Warning "The incomplete staging directory could not be removed and is left in place: $StagingRoot ($_)"
    }
}

<#
.SYNOPSIS
    Moves a validated staging payload into the destination.
.DESCRIPTION
    Renames the existing payload aside, renames staging into place, then deletes the
    old copy. If the second rename fails, the old payload is renamed back, so the
    destination is either the previous payload or the new one and never a mixture.
#>
function Complete-PayloadPromotion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$StagingRoot,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DestinationRoot
    )

    $staging = [System.IO.Path]::GetFullPath($StagingRoot)
    $destination = [System.IO.Path]::GetFullPath($DestinationRoot)
    if (-not (Test-Path -LiteralPath $staging -PathType Container)) {
        throw "The staged payload is missing: $staging"
    }

    # Re-check immediately before the swap. The up-front checks run before a build
    # that takes minutes, so this closes the window in which the dashboard was
    # started - or a link was created - in between.
    Assert-PayloadDestinationReplaceable -DestinationRoot $destination -Stage "at the promotion step"
    Assert-PayloadFreeOfReparsePoint -Root $staging -Stage "in the staged payload at the promotion step"

    $previous = "$destination.previous-$([Guid]::NewGuid().ToString('N'))"
    $movedAside = $false
    if (Test-Path -LiteralPath $destination) {
        Move-Item -LiteralPath $destination -Destination $previous
        $movedAside = $true
    }

    try {
        Move-Item -LiteralPath $staging -Destination $destination
    }
    catch {
        # Every failure after the rename-aside routes through here. Once the
        # previous payload is sitting under a generated name the operator has
        # never seen, the only thing that makes it recoverable is an error that
        # says where it went - so no post-rename path is allowed to fall through
        # to a bare rethrow.
        $promotionError = $_
        if (-not $movedAside) {
            # Nothing was renamed aside, so the destination is exactly as the
            # operator left it and there is no recovery state to report.
            throw
        }

        $restored = $false
        $restorationError = $null
        $blockedReason = $null
        if (Test-Path -LiteralPath $destination) {
            # The destination came back between the rename-aside and this move.
            # Restoring on top of it would move the previous payload inside the
            # new directory rather than back into place, so it is refused and
            # reported instead of being attempted blindly.
            $blockedReason = "the destination path exists again - something recreated it after the previous payload was renamed aside - and restoring over it would have nested the previous payload inside it instead of putting it back"
        }
        else {
            try {
                Move-Item -LiteralPath $previous -Destination $destination
                $restored = $true
            }
            catch {
                $restorationError = $_
            }
        }

        if ($restored) {
            # The destination holds the previous payload again, so the promotion
            # failure is the whole story.
            throw $promotionError
        }

        $cause = if ($null -ne $restorationError) {
            "Restoration error: $($restorationError.Exception.Message)"
        }
        else {
            "Restoration was not attempted because $blockedReason."
        }
        $destinationState = if (Test-Path -LiteralPath $destination) {
            "Something exists at the destination, but it is NOT your previous payload: $destination"
        }
        else {
            "No payload is currently at: $destination"
        }

        # Both causes are preserved: the promotion failure as the inner
        # exception, the restoration outcome in the message. Neither tree is
        # deleted - the previous payload because it is the only copy, staging
        # because the operator may need it to diagnose why promotion failed.
        $failure = New-Object System.InvalidOperationException(@"
PUBLICATION FAILED AND THE PREVIOUS PAYLOAD IS NOT BACK IN PLACE.
$destinationState

Your previous payload is INTACT and retained at:
    $previous
The staged replacement has been left at:
    $staging

Recover manually by removing whatever is at the destination and renaming the
previous payload back:

    Move-Item -LiteralPath '$previous' -Destination '$destination'

Neither directory will be deleted automatically.

Promotion error:   $($promotionError.Exception.Message)
$cause
"@, $promotionError.Exception)
        $failure.Data["PayloadPromotion.RetainStaging"] = $true
        $failure.Data["PayloadPromotion.RecoveryPath"] = $previous
        $failure.Data["PayloadPromotion.StagingPath"] = $staging
        throw $failure
    }

    if ($movedAside) {
        try {
            Remove-PayloadTree -Path $previous
        }
        catch {
            Write-Warning "The new payload is in place, but the previous payload could not be removed and is left at: $previous ($_)"
        }
    }

    return $destination
}

<#
.SYNOPSIS
    Builds and validates a payload in staging, then promotes it.
.DESCRIPTION
    The whole safe-replacement contract in one place, so the publish script and its
    regression tests exercise the same ordering rather than two similar copies:

      1. prove the destination is replaceable - before anything is built;
      2. build into a unique staging directory;
      3. validate staging;
      4. re-prove the destination and promote;
      5. on any failure, remove only staging and leave the destination untouched.

.PARAMETER BuildStaging
    Receives the staging path and populates it.
.PARAMETER ValidateStaging
    Receives the staging path. Throwing rejects the payload and preserves the
    existing destination.
#>
function Invoke-PayloadReplacement {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DestinationRoot,

        [Parameter(Mandatory)]
        [scriptblock]$BuildStaging,

        [scriptblock]$ValidateStaging
    )

    $destination = [System.IO.Path]::GetFullPath($DestinationRoot)
    Assert-PayloadDestinationReplaceable -DestinationRoot $destination -Stage "before publishing"

    $staging = New-PayloadStagingDirectory -DestinationRoot $destination
    $promoted = $false
    $retainStaging = $false
    try {
        & $BuildStaging $staging
        # A build can materialise a link (a stray junction in a project output, a
        # symlinked dependency). Refuse it here, before anything hashes or
        # validates through it.
        Assert-PayloadFreeOfReparsePoint -Root $staging -Stage "in the staged payload"
        if ($null -ne $ValidateStaging) {
            & $ValidateStaging $staging
        }

        $result = Complete-PayloadPromotion -StagingRoot $staging -DestinationRoot $destination
        $promoted = $true
        return $result
    }
    catch {
        # A failed restoration keeps staging on disk deliberately; deleting the
        # candidate payload while the operator is recovering by hand removes
        # evidence they may need.
        if ($_.Exception.Data.Contains("PayloadPromotion.RetainStaging")) {
            $retainStaging = $true
        }
        throw
    }
    finally {
        if (-not $promoted -and -not $retainStaging) {
            Remove-PayloadStagingDirectory -StagingRoot $staging
        }
    }
}
