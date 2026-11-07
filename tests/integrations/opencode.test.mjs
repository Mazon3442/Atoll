import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../../src/Atoll.Core/Integrations/opencode.js", import.meta.url), "utf8");

async function adapter(t, { missingExe = false, subscriptionError = false } = {}) {
  const messages = [], warnings = [], disposed = [];
  const hooks = new Map();
  let deliver, signal;
  const context = vm.createContext({
    __ATOLL_HOOK_EXE__: "C:/ATOLL/Atoll.ClaudeHook.exe", setTimeout, clearTimeout, AbortController,
    Plugin: { define: (definition) => definition },
    console: { warn: (message) => warnings.push(message) },
    spawn(exe, args, options) {
      assert.equal(exe, "C:/ATOLL/Atoll.ClaudeHook.exe");
      assert.equal(options.windowsHide, true);
      assert.deepEqual(Array.from(args), ["--agent", "opencode"]);
      if (missingExe) throw new Error("not installed");
      const child = new EventEmitter();
      child.stdin = new EventEmitter();
      child.stdin.end = (data) => {
        messages.push(JSON.parse(data));
        queueMicrotask(() => child.emit("close", 0));
      };
      child.kill = () => child.emit("close", 0);
      return child;
    },
  });
  vm.runInContext(source.replace(/^import .*$/gm, "").replace("export default", "globalThis.definition ="), context);
  assert.equal(context.definition.id, "atoll");
  const cleanup = await context.definition.setup({
    location: { directory: "C:/project" },
    tool: { async hook(name, callback) {
      assert.ok(["execute.before", "execute.after"].includes(name));
      hooks.set(name, callback);
      return { async dispose() { hooks.delete(name); disposed.push(name); } };
    } },
    event: { async *subscribe(options) {
      signal = options.signal;
      if (subscriptionError) throw new Error("disconnected");
      const abort = () => deliver?.();
      signal.addEventListener("abort", abort, { once: true });
      try {
        while (!signal.aborted) {
          const item = await new Promise(resolve => { deliver = resolve; });
          if (!item) return;
          // Acknowledge only after the adapter finishes processing this event.
          try { yield item.event; } finally { item.resolve(); }
        }
      } finally { signal.removeEventListener("abort", abort); }
    } },
  });
  t.after(() => { if (!signal.aborted) return cleanup(); });
  return {
    messages, warnings, hooks, disposed, cleanup,
    get signal() { return signal; },
    event(type, data = {}) {
      return new Promise(resolve => deliver({ event: { type, data: { sessionID: "s1", ...data } }, resolve }));
    },
  };
}

test("v2 session events start and finish once, with permission and safe tool summaries", async t => {
  const { event, hooks, messages } = await adapter(t);
  await event("session.created");
  await event("session.execution.succeeded");
  await event("session.execution.started");
  await event("session.execution.started");
  await hooks.get("execute.before")({ sessionID: "s1", tool: "write",
    input: { filePath: "app.cs", content: "private file content", description: "x".repeat(400) } });
  await event("permission.asked");
  await event("permission.replied");
  await hooks.get("execute.after")({ sessionID: "s1", tool: "write", status: "completed",
    result: { content: "private tool output" } });
  await event("session.execution.succeeded");
  await event("session.execution.succeeded");
  assert.deepEqual(messages.map(m => m.hook_event_name),
    ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PostToolUse", "Stop"]);
  assert.ok(messages.every(m => m.session_id === "s1" && m.cwd === "C:/project"));
  assert.equal(messages[2].tool_input.filePath, "app.cs");
  assert.equal(messages[2].tool_input.description.length, 300);
  assert.equal(JSON.stringify(messages).includes("private"), false);
});

for (const [type, expected] of [["failed", "Error"], ["interrupted", "Interrupt"]]) {
  test(`v2 ${type} executions and deletion never send a success notification`, async t => {
    const { event, messages } = await adapter(t);
    await event("session.execution.started");
    await event(`session.execution.${type}`);
    await event("session.execution.succeeded");
    await event("session.deleted");
    assert.deepEqual(messages.map(m => m.hook_event_name), ["UserPromptSubmit", expected, "SessionEnd"]);
  });
}

test("tool activity can start a session when the adapter loads mid-execution", async t => {
  const { event, hooks, messages } = await adapter(t);
  await hooks.get("execute.before")({ sessionID: "s1", tool: "read", input: { filePath: "app.cs" } });
  await event("session.execution.succeeded");
  assert.deepEqual(messages.map(m => m.hook_event_name), ["UserPromptSubmit", "PreToolUse", "Stop"]);
});

test("concurrent v2 sessions keep independent completion state", async t => {
  const { event, messages } = await adapter(t);
  await event("session.execution.started");
  await event("session.execution.started", { sessionID: "s2" });
  await event("session.execution.succeeded");
  await event("session.execution.succeeded", { sessionID: "s2" });
  assert.deepEqual(messages.filter(m => m.hook_event_name === "Stop").map(m => m.session_id), ["s1", "s2"]);
});

test("a missing hook executable cannot break an OpenCode session", async t => {
  const { event, hooks } = await adapter(t, { missingExe: true });
  await hooks.get("execute.before")({ sessionID: "s1", tool: "read", input: {} });
  await event("session.execution.succeeded");
});

test("unloading aborts the subscription and disposes both tool hooks", async t => {
  const plugin = await adapter(t);
  await plugin.event("session.execution.started");
  await plugin.cleanup();
  assert.equal(plugin.signal.aborted, true);
  assert.equal(plugin.hooks.size, 0);
  assert.deepEqual(plugin.disposed, ["execute.before", "execute.after"]);
  assert.deepEqual(plugin.warnings, []);
  assert.equal(plugin.messages.length, 1);
});

test("a rejected event subscription is handled without breaking the host", async t => {
  const plugin = await adapter(t, { subscriptionError: true });
  await new Promise(setImmediate);
  await plugin.cleanup();
  assert.equal(plugin.warnings.length, 1);
});
