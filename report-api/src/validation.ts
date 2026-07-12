export const MAX_REPORT_BYTES = 2 * 1024 * 1024;

export interface CompatibilitySummary {
  appVersion: string;
  osBuild: string | null;
  cpuName: string | null;
  gpuNames: string[];
  motherboardName: string | null;
  capabilityCounts: Record<string, number>;
}

type JsonObject = Record<string, unknown>;

const sensitiveKeyFragments = [
  "serial",
  "hostname",
  "computername",
  "username",
  "installpath",
  "macaddress",
  "ipaddress",
  "ssid"
];

const windowsUserPath = /[a-z]:\\users\\[^\\]+\\/i;
const uncPath = /\\\\[^\\\s]+\\/;
const macAddress = /\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b/i;
const ipv4Address = /\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b/;

export function validateClientId(value: string | null): string | null {
  if (value === null || !/^[a-f0-9-]{32,64}$/i.test(value)) {
    return null;
  }

  return value.toLowerCase();
}

export function validateApprovedReport(value: unknown): value is JsonObject {
  if (!isObject(value)) {
    return false;
  }

  if (value.schemaVersion !== 1 || value.userApproved !== true) {
    return false;
  }

  if (typeof value.appVersion !== "string" || value.appVersion.length === 0 || value.appVersion.length > 64) {
    return false;
  }

  if (!isObject(value.snapshot)
    || !isBoundedObjectArray(value.snapshot.devices, 256)
    || !isBoundedObjectArray(value.snapshot.capabilities, 2048)
    || !isBoundedObjectArray(value.snapshot.sensors, 4096)
    || !isBoundedObjectArray(value.snapshot.conflicts, 128)
    || !isBoundedObjectArray(value.snapshot.warnings, 256)
    || !isBoundedObjectArray(value.snapshot.adapterHealth, 128)) {
    return false;
  }

  const containsNetworkInventory = value.snapshot.devices.some(
    (device) => String(device.kind).toLowerCase() === "network"
  );
  return !containsNetworkInventory && !containsSensitiveKey(value) && !containsSensitiveText(value);
}

export function extractSummary(report: JsonObject): CompatibilitySummary {
  const snapshot = report.snapshot as JsonObject;
  const devices = (snapshot.devices as unknown[]).filter(isObject);
  const capabilities = (snapshot.capabilities as unknown[]).filter(isObject);
  const findDevice = (kind: string): JsonObject | undefined =>
    devices.find((device) => String(device.kind).toLowerCase() === kind.toLowerCase());
  const gpuNames = devices
    .filter((device) => String(device.kind).toLowerCase() === "gpu")
    .map((device) => String(device.name ?? "Unknown GPU"))
    .slice(0, 8);
  const capabilityCounts: Record<string, number> = {};
  for (const capability of capabilities) {
    const state = String(capability.state ?? "Unknown");
    capabilityCounts[state] = (capabilityCounts[state] ?? 0) + 1;
  }

  const operatingSystem = findDevice("OperatingSystem");
  const cpu = findDevice("Cpu");
  const motherboard = findDevice("Motherboard");
  return {
    appVersion: String(report.appVersion),
    osBuild: readProperty(operatingSystem, "buildNumber"),
    cpuName: readName(cpu),
    gpuNames,
    motherboardName: readName(motherboard),
    capabilityCounts
  };
}

export function containsSensitiveKey(value: unknown): boolean {
  if (Array.isArray(value)) {
    return value.some(containsSensitiveKey);
  }

  if (!isObject(value)) {
    return false;
  }

  for (const [key, child] of Object.entries(value)) {
    const normalised = key.toLowerCase().replaceAll("_", "");
    if (sensitiveKeyFragments.some((fragment) => normalised.includes(fragment))) {
      return true;
    }

    if (containsSensitiveKey(child)) {
      return true;
    }
  }

  return false;
}

export function containsSensitiveText(value: unknown, keyHint = ""): boolean {
  if (typeof value === "string") {
    const versionField = keyHint.toLowerCase().includes("version")
      || keyHint.toLowerCase().includes("build");
    return windowsUserPath.test(value)
      || uncPath.test(value)
      || macAddress.test(value)
      || (!versionField && ipv4Address.test(value));
  }

  if (Array.isArray(value)) {
    return value.some((child) => containsSensitiveText(child, keyHint));
  }

  return isObject(value) && Object.entries(value).some(
    ([key, child]) => containsSensitiveText(child, key)
  );
}

function readName(device: JsonObject | undefined): string | null {
  return device && typeof device.name === "string" ? device.name.slice(0, 256) : null;
}

function readProperty(device: JsonObject | undefined, key: string): string | null {
  if (!device || !isObject(device.properties)) {
    return null;
  }

  const value = device.properties[key];
  return typeof value === "string" ? value.slice(0, 128) : null;
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isBoundedObjectArray(value: unknown, maximum: number): value is JsonObject[] {
  return Array.isArray(value) && value.length <= maximum && value.every(isObject);
}
