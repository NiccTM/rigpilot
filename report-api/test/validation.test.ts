import { describe, expect, it } from "vitest";
import {
  containsSensitiveKey,
  containsSensitiveText,
  extractSummary,
  validateApprovedReport,
  validateClientId
} from "../src/validation";

const validReport = {
  schemaVersion: 1,
  userApproved: true,
  appVersion: "0.2.0.0",
  snapshot: {
    devices: [
      { kind: "OperatingSystem", name: "Windows 11", properties: { buildNumber: "26200" } },
      { kind: "Cpu", name: "Ryzen 7 5800X", properties: {} },
      { kind: "Gpu", name: "RTX 3090", properties: {} },
      { kind: "Motherboard", name: "ROG Strix X570-E", properties: {} }
    ],
    capabilities: [{ state: "Verified" }, { state: "ReadOnly" }, { state: "ReadOnly" }],
    sensors: [] as Array<Record<string, unknown>>,
    conflicts: [] as Array<Record<string, unknown>>,
    warnings: [] as Array<Record<string, unknown>>,
    adapterHealth: [] as Array<Record<string, unknown>>
  }
};

describe("report validation", () => {
  it("accepts approved redacted reports", () => {
    expect(validateApprovedReport(validReport)).toBe(true);
  });

  it("rejects unapproved reports", () => {
    expect(validateApprovedReport({ ...validReport, userApproved: false })).toBe(false);
  });

  it("rejects sensitive keys at any depth", () => {
    const report = structuredClone(validReport) as Record<string, unknown>;
    (report.snapshot as Record<string, unknown>).serialNumber = "secret";
    expect(containsSensitiveKey(report)).toBe(true);
    expect(validateApprovedReport(report)).toBe(false);
  });

  it("rejects sensitive text and network inventory", () => {
    const withAddress = structuredClone(validReport);
    withAddress.snapshot.warnings.push({ message: "address 192.168.1.2" });
    expect(containsSensitiveText(withAddress)).toBe(true);
    expect(validateApprovedReport(withAddress)).toBe(false);

    const withNetwork = structuredClone(validReport);
    withNetwork.snapshot.devices.push({ kind: "Network", name: "Private adapter", properties: {} });
    expect(validateApprovedReport(withNetwork)).toBe(false);
  });

  it("does not mistake dotted version fields for IP addresses", () => {
    expect(containsSensitiveText(validReport)).toBe(false);
    expect(validateApprovedReport(validReport)).toBe(true);
  });

  it("extracts bounded compatibility summary", () => {
    const summary = extractSummary(validReport);
    expect(summary.osBuild).toBe("26200");
    expect(summary.cpuName).toBe("Ryzen 7 5800X");
    expect(summary.gpuNames).toEqual(["RTX 3090"]);
    expect(summary.capabilityCounts).toEqual({ Verified: 1, ReadOnly: 2 });
  });

  it("validates anonymous installation identifiers", () => {
    expect(validateClientId("0123456789abcdef0123456789abcdef")).not.toBeNull();
    expect(validateClientId("short")).toBeNull();
  });
});
