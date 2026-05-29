import assert from "node:assert/strict";
import test from "node:test";
import { buildRevitApiUrl, normalizeRevitHost, parseTimeoutMs, resolveRevitBridgeConfig, validateRevitAction } from "../dist/bridge.js";

test("normalizes localhost bridge URL without trailing slash", () => {
  assert.equal(normalizeRevitHost("http://localhost:6543/"), "http://localhost:6543");
});

test("rejects non-http bridge URLs", () => {
  assert.throws(() => normalizeRevitHost("https://localhost:6543"), /must use http/);
});

test("rejects remote bridge hosts by default", () => {
  assert.throws(() => normalizeRevitHost("http://192.168.1.10:6543"), /localhost\/127\.0\.0\.1/);
});

test("allows remote bridge hosts only when explicitly enabled", () => {
  assert.equal(normalizeRevitHost("http://192.168.1.10:6543/", true), "http://192.168.1.10:6543");
});

test("normalizes bridge URL to origin only", () => {
  assert.equal(normalizeRevitHost("http://localhost:6543/api/model/info?debug=1"), "http://localhost:6543");
});

test("rejects bridge URLs with credentials", () => {
  assert.throws(() => normalizeRevitHost("http://user:pass@localhost:6543"), /credentials/);
});

test("validates timeout configuration", () => {
  assert.equal(parseTimeoutMs("120000"), 120000);
  assert.throws(() => parseTimeoutMs("500"), />= 1000/);
  assert.throws(() => parseTimeoutMs("abc"), />= 1000/);
  assert.throws(() => parseTimeoutMs("900000"), /<= 600000/);
});

test("resolves full bridge config from environment", () => {
  assert.deepEqual(
    resolveRevitBridgeConfig({ REVIT_HOST: "http://127.0.0.1:6543/", REVIT_TIMEOUT_MS: "30000" }),
    { host: "http://127.0.0.1:6543", timeoutMs: 30000 }
  );
});

test("validates safe Revit API actions", () => {
  assert.equal(validateRevitAction("model/info"), "model/info");
  assert.equal(validateRevitAction("views/create-plans-for-levels"), "views/create-plans-for-levels");
  assert.throws(() => validateRevitAction("/model/info"), /Invalid Revit action/);
  assert.throws(() => validateRevitAction("../healthz"), /Invalid Revit action/);
  assert.throws(() => validateRevitAction("model//info"), /Invalid Revit action/);
  assert.throws(() => validateRevitAction("model/info?debug=1"), /Invalid Revit action/);
  assert.throws(() => validateRevitAction("model info"), /Invalid Revit action/);
});

test("builds Revit API URLs from validated action names", () => {
  assert.equal(buildRevitApiUrl("http://localhost:6543", "model/info"), "http://localhost:6543/api/model/info");
  assert.throws(() => buildRevitApiUrl("http://localhost:6543", "../healthz"), /Invalid Revit action/);
});
