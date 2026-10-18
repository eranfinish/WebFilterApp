// Validates every scenario file against the JSON schema, then checks what a
// schema cannot: references to members/devices/rules, unique ids, a clock that
// never moves backwards, and decision/reason consistency.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const readJson = (p) => JSON.parse(readFileSync(p, "utf8"));

const schema = readJson(join(root, "schema", "scenario.schema.json"));
const ajv = new Ajv2020({ allErrors: true, strict: false, allowUnionTypes: true });
ajv.addSchema(schema);
const validateSuite = ajv.getSchema(schema.$id);

const ref = (def) => ({ $ref: `${schema.$id}#/$defs/${def}` });
const arrayOf = (def) => ({ type: "array", items: ref(def) });
const validateFixture = ajv.compile({
  type: "object",
  required: ["timezone", "members", "devices", "categories", "memberCategories", "rules", "schedules", "dailyLimits", "youtubeChannels", "supportedBrowsers", "appRules"],
  properties: {
    description: { type: "string" },
    timezone: { type: "string" },
    members: arrayOf("member"),
    devices: arrayOf("device"),
    categories: { type: "object", additionalProperties: { type: "array", items: { type: "string" } } },
    memberCategories: arrayOf("memberCategory"),
    rules: arrayOf("rule"),
    schedules: arrayOf("schedule"),
    dailyLimits: arrayOf("dailyLimit"),
    youtubeChannels: arrayOf("youtubeChannel"),
    supportedBrowsers: { type: "array", items: { type: "string" } },
    appRules: arrayOf("appRule"),
  },
  additionalProperties: false,
});

const errors = [];
const fail = (where, msg) => errors.push(`${where}: ${msg}`);
const ajvErrors = (errs) => errs.map((e) => `${e.instancePath || "/"} ${e.message}`).join("; ");

// ---- fixtures ----
const fixtures = {};
for (const f of readdirSync(join(root, "fixtures")).filter((f) => f.endsWith(".json"))) {
  const name = f.replace(/\.json$/, "");
  const data = readJson(join(root, "fixtures", f));
  if (!validateFixture(data)) fail(`fixtures/${f}`, ajvErrors(validateFixture.errors));
  fixtures[name] = data;
}

// ---- time helpers (naive local wall-clock, compared as strings) ----
const pad = (s) => (s.length === 5 ? `${s}:00` : s);
function toLocal(value, current) {
  if (value.includes("T")) { const [d, t] = value.split("T"); return `${d}T${pad(t)}`; }
  return `${current.split("T")[0]}T${pad(value)}`;
}

// ---- scenario files ----
const files = [];
(function walk(dir) {
  for (const f of readdirSync(dir)) {
    const p = join(dir, f);
    if (statSync(p).isDirectory()) walk(p);
    else if (f.endsWith(".json")) files.push(p);
  }
})(join(root, "scenarios"));

const scenarioIds = new Map();
let scenarioCount = 0, stepCount = 0, caseCount = 0;

for (const file of files) {
  const rel = relative(root, file).replaceAll("\\", "/");
  let suite;
  try { suite = readJson(file); } catch (e) { fail(rel, `invalid JSON: ${e.message}`); continue; }
  if (!validateSuite(suite)) { fail(rel, ajvErrors(validateSuite.errors)); continue; }

  const fixture = fixtures[suite.fixture ?? "family-base"];
  if (!fixture) { fail(rel, `unknown fixture '${suite.fixture}'`); continue; }

  for (const sc of suite.scenarios) {
    const where = `${rel} ${sc.id}`;
    scenarioCount++;
    if (scenarioIds.has(sc.id)) fail(where, `duplicate scenario id (also in ${scenarioIds.get(sc.id)})`);
    scenarioIds.set(sc.id, rel);

    // Effective family = fixture + setup (merge rules from README section 3).
    const setup = sc.setup ?? {};
    const members = new Map(fixture.members.map((m) => [m.id, { ...m }]));
    for (const m of setup.members ?? []) {
      if (!members.has(m.id)) fail(where, `setup patches unknown member '${m.id}'`);
      else Object.assign(members.get(m.id), m);
    }
    const devices = new Map(fixture.devices.map((d) => [d.id, d]));
    const ruleIds = new Set();
    const addRuleId = (id, w) => { if (ruleIds.has(id)) fail(w, `duplicate rule id '${id}'`); ruleIds.add(id); };
    for (const r of [...fixture.rules, ...(setup.rules ?? [])]) addRuleId(r.id, where);
    for (const a of [...fixture.appRules, ...(setup.appRules ?? [])]) addRuleId(a.id, where);

    const checkMember = (id, w) => { if (id !== undefined && !members.has(id)) fail(w, `unknown member '${id}'`); };
    const checkDevice = (id, w) => { if (id !== undefined && !devices.has(id)) fail(w, `unknown device '${id}'`); };
    const checkMemberOnDevice = (member, device, w) => {
      if (!member || !device || !devices.has(device)) return;
      if (!devices.get(device).accounts.some((a) => a.member === member)) fail(w, `member '${member}' has no account on '${device}'`);
    };
    for (const r of setup.rules ?? []) checkMember(r.member, `${where} setup.rules`);
    for (const a of setup.appRules ?? []) checkMember(a.member, `${where} setup.appRules`);
    for (const s of [...(setup.schedules ?? []), ...(setup.dailyLimits ?? []), ...(setup.youtubeChannels ?? [])]) checkMember(s.member, `${where} setup`);
    for (const c of setup.memberCategories ?? []) {
      checkMember(c.member, `${where} setup.memberCategories`);
      if (!fixture.categories[c.category]) fail(`${where} setup.memberCategories`, `unknown category '${c.category}'`);
    }

    let now = toLocal(sc.start, "1970-01-01T00:00");
    const advance = (value, w) => {
      const t = toLocal(value, now);
      if (t < now) fail(w, `clock moves backwards: ${t} < ${now}`);
      else now = t;
    };

    const checkExpect = (expect, w) => {
      const { decision, reason, ruleId } = expect;
      const prefix = { allow: "allowed_", block: "blocked_", redirect: "redirect_" }[decision];
      if (decision === "not_applicable") { if (reason) fail(w, "not_applicable must not have a reason"); }
      else if (!reason) fail(w, `decision '${decision}' needs a reason`);
      else if (!reason.startsWith(prefix)) fail(w, `reason '${reason}' does not fit decision '${decision}'`);
      if (ruleId && !ruleIds.has(ruleId)) fail(w, `unknown ruleId '${ruleId}'`);
    };

    sc.steps.forEach((step, i) => {
      const w = `${where} step ${i + 1} (${step.do})`;
      stepCount++;
      if (step.at) advance(step.at, w);
      checkMember(step.member, w);
      checkDevice(step.device, w);
      checkMemberOnDevice(step.member, step.device, w);

      switch (step.do) {
        case "navigate":
          if (step.account) {
            const dev = devices.get(step.device);
            if (dev && !dev.accounts.some((a) => a.name === step.account)) fail(w, `no account '${step.account}' on '${step.device}'`);
          }
          checkExpect(step.expect, w);
          break;
        case "checkUrls":
          step.cases.forEach((c, j) => { caseCount++; checkExpect(c.expect, `${w} case ${j + 1}`); });
          break;
        case "parseYouTube":
          caseCount += step.cases.length;
          break;
        case "youtube":
        case "app":
          checkExpect(step.expect, w);
          break;
        case "browse": {
          let latest = now;
          for (const s of step.sessions) {
            checkMember(s.member, w); checkDevice(s.device, w); checkMemberOnDevice(s.member, s.device, w);
            const from = toLocal(s.from, now), to = toLocal(s.to, now);
            if (from < now) fail(w, `session starts in the past: ${from} < ${now}`);
            if (to <= from) fail(w, `session ends before it starts: ${s.from}-${s.to}`);
            if (to > latest) latest = to;
          }
          now = latest;
          break;
        }
        case "setTime":
          advance(step.to, w);
          break;
        case "admin":
          if (step.action === "addRule") { checkMember(step.rule.member, w); addRuleId(step.rule.id, w); }
          if (step.action === "removeRule" && !ruleIds.has(step.ruleId)) fail(w, `removes unknown rule '${step.ruleId}'`);
          if (step.action === "setMember") checkMember(step.patch.id, w);
          if (step.until) { const u = toLocal(step.until, now); if (u <= now) fail(w, `'until' is not in the future`); }
          break;
        case "expectTime":
          if (step.remainingMinutes !== undefined && step.limitMinutes !== undefined && step.usedMinutes !== undefined
              && step.remainingMinutes !== Math.max(0, step.limitMinutes - step.usedMinutes))
            fail(w, "remainingMinutes != max(0, limitMinutes - usedMinutes)");
          break;
        case "expectEvents":
          for (const item of step.expect.items ?? []) checkDevice(item.device, w);
          break;
      }
    });
  }
}

if (errors.length) {
  console.error(`✗ ${errors.length} problem(s):`);
  for (const e of errors) console.error(`  - ${e}`);
  process.exit(1);
}
console.log(`✓ ${files.length} files, ${scenarioCount} scenarios, ${stepCount} steps, ${caseCount} table cases — all valid`);
