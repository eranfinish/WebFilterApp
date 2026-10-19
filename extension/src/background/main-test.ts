// Test-build service worker entry (dist-test/ only): the production wiring plus test hooks.
import { installTestHooks, readInstallTypeOverride } from "./test-hooks";
import { createBackground } from "./wiring";

const controller = createBackground({ installTypeOverride: readInstallTypeOverride });
installTestHooks(controller);
