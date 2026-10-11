import { canvasResolution, looksLowEnd, readPerfChoice, resolvePerfMode } from "./perf";

const setNavigator = (props: Record<string, unknown>) => {
  for (const [key, value] of Object.entries(props)) Object.defineProperty(navigator, key, { value, configurable: true });
};

describe("modo de rendimiento", () => {
  afterEach(() => {
    window.localStorage.clear();
    delete document.documentElement.dataset.perf;
    setNavigator({ hardwareConcurrency: 8, deviceMemory: undefined });
  });

  it("detects a low-end phone by its core count or memory", () => {
    setNavigator({ hardwareConcurrency: 8, deviceMemory: undefined });
    expect(looksLowEnd()).toBe(false);
    setNavigator({ hardwareConcurrency: 4 });
    expect(looksLowEnd()).toBe(true);
    setNavigator({ hardwareConcurrency: 8, deviceMemory: 2 });
    expect(looksLowEnd()).toBe(true);
  });

  it("lets an explicit choice win over the detection", () => {
    setNavigator({ hardwareConcurrency: 2 });
    expect(resolvePerfMode("auto")).toBe("lite");
    expect(resolvePerfMode("full")).toBe("full");
    setNavigator({ hardwareConcurrency: 8 });
    expect(resolvePerfMode("lite")).toBe("lite");
  });

  it("defaults to auto and ignores garbage in storage", () => {
    expect(readPerfChoice()).toBe("auto");
    window.localStorage.setItem("pleno.perf", "turbo");
    expect(readPerfChoice()).toBe("auto");
    window.localStorage.setItem("pleno.perf", "lite");
    expect(readPerfChoice()).toBe("lite");
  });

  it("renders canvases at 1x in lite mode and never above 2x otherwise", () => {
    Object.defineProperty(window, "devicePixelRatio", { value: 3, configurable: true });
    document.documentElement.dataset.perf = "full";
    expect(canvasResolution()).toBe(2);
    document.documentElement.dataset.perf = "lite";
    expect(canvasResolution()).toBe(1);
  });
});
