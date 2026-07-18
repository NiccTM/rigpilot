using PCHelper.App;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class HardwareControlUiStateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void UiCommitsOnlyFullyVerifiedServiceResult(
        bool responseSuccess,
        bool allVerified,
        bool expected)
    {
        HardwareControlTransactionResult result = new(
            Armed: true,
            AllRequestedFamiliesVerified: allVerified,
            RecoveryRequired: !allVerified,
            [],
            "test");

        Assert.Equal(
            expected,
            MainViewModel.ShouldCommitHardwareControlState(responseSuccess, result, requestedArmed: true));
    }

    [Fact]
    public void DisarmCommitsOnlyAfterDefaultReadBack()
    {
        HardwareControlTransactionResult result = new(
            Armed: false,
            AllRequestedFamiliesVerified: true,
            RecoveryRequired: false,
            [],
            "defaults verified");

        Assert.True(MainViewModel.ShouldCommitHardwareControlState(true, result, requestedArmed: false));
    }
}
