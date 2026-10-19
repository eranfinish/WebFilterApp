// Production service worker entry. The test build uses main-test.ts instead (adds test hooks).
import { createBackground } from "./wiring";

createBackground();
