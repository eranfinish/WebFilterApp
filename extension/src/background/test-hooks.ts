import type { Controller } from "./controller";
import { FakeHost, type FakeHostScript } from "./fake-host";
import { nativeConnector } from "./native-client";

/** chrome.storage.session key holding the simulated install type (session storage is not visible to content scripts). */
export const INSTALL_TYPE_OVERRIDE_KEY = "test.installType";

export async function readInstallTypeOverride(): Promise<string | null> {
  const value = (await chrome.storage.session.get(INSTALL_TYPE_OVERRIDE_KEY))[INSTALL_TYPE_OVERRIDE_KEY];
  return typeof value === "string" ? value : null;
}

/**
 * Test build only (`npm run build:test` → dist-test/): exposes `globalThis.wfTest` in the service worker so Playwright
 * can simulate a policy install (an unpacked extension always reports installType "development") and drive an
 * in-worker fake native host. The production bundle is checked by the build not to contain this code.
 */
export function installTestHooks(controller: Controller): void {
  let fake: FakeHost | null = null;
  const api = {
    marker: "wf-test-hooks",
    async simulatePolicyInstall(installType = "admin"): Promise<void> {
      await chrome.storage.session.set({ [INSTALL_TYPE_OVERRIDE_KEY]: installType });
      await controller.start();
    },
    async clearPolicyOverride(): Promise<void> {
      await chrome.storage.session.remove(INSTALL_TYPE_OVERRIDE_KEY);
      await controller.start();
    },
    async useFakeHost(script: FakeHostScript = {}): Promise<void> {
      fake = new FakeHost(script);
      controller.setConnector(fake.connector);
      await controller.start();
    },
    async useRealHost(): Promise<void> {
      fake = null;
      controller.setConnector(nativeConnector);
      await controller.start();
    },
    setScript(script: FakeHostScript): void {
      if (fake) fake.script = script;
    },
    push(message: Record<string, unknown>): void {
      fake?.push(message);
    },
    hangUp(): void {
      fake?.hangUp();
    },
    received(): unknown[] {
      return fake ? [...fake.received] : [];
    },
    connections(): number {
      return fake?.connections ?? 0;
    },
    status() {
      return controller.snapshot();
    },
    async whenReady(timeoutMs = 5_000): Promise<boolean> {
      const until = Date.now() + timeoutMs;
      while (Date.now() < until) {
        if (controller.ready) return true;
        await new Promise((r) => setTimeout(r, 50));
      }
      return controller.ready;
    },
    async heartbeat(): Promise<void> {
      await controller.heartbeat();
    },
  };
  (globalThis as unknown as { wfTest: typeof api }).wfTest = api;
}
