import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../../src/Atoll.Core/Integrations/opencode.js", import.meta.url), "utf8");

async function adapter() {
  const messages = [];
  const context = vm.createContext({
    __ATOLL_HOOK_EXE__: "C:/ATOLL/Atoll.ClaudeHook.exe", setTimeout, clearTimeout,
    spawn(exe, args, options) {
      assert.equal(options.windowsHide, true);
      assert.deepEqual(Array.from(args), ["--agent", "opencode"]);
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
  vm.runInContext(source.replace(/^import .*$/m, "").replace("export const", "const")
    + "\nglobalThis.createPlugin = AtollPlugin;", context);
  return { plugin: await context.createPlugin({ directory: "C:/project" }), messages };
}

test("one turn starts and finishes once, with permission and tool summaries", async () => {
  const { plugin, messages } = await adapter();
  const event = (type, extra = {}) => plugin.event({ event: { type, properties: { sessionID: "s1", ...extra } } });
  await event("session.idle");
  assert.equal(messages.length, 0);
  await event("session.status", { status: { type: "busy" } });
  await event("session.status", { status: { type: "busy" } });
  await plugin["tool.execute.before"]({ sessionID: "s1", tool: "write" },
    { args: { filePath: "app.cs", content: "private file content" } });
  await event("permission.asked");
  await event("permission.replied");
  await plugin["tool.execute.after"]({ sessionID: "s1", tool: "write" });
  await event("session.status", { status: { type: "idle" } });
  await event("session.idle");
  assert.deepEqual(messages.map(m => m.hook_event_name),
    ["UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PostToolUse", "Stop"]);
  assert.equal(messages[1].tool_input.filePath, "app.cs");
  assert.equal(JSON.stringify(messages).includes("private file content"), false);
});

test("errors and deletion never send a success notification", async () => {
  const { plugin, messages } = await adapter();
  await plugin.event({ event: { type: "session.status", properties: { sessionID: "s1", status: { type: "busy" } } } });
  await plugin.event({ event: { type: "session.error", properties: { sessionID: "s1" } } });
  await plugin.event({ event: { type: "session.idle", properties: { sessionID: "s1" } } });
  await plugin.event({ event: { type: "session.deleted", properties: { info: { id: "s1" } } } });
  assert.deepEqual(messages.map(m => m.hook_event_name), ["UserPromptSubmit", "Error", "SessionEnd"]);
});

test("a missing hook executable cannot break an OpenCode session", async () => {
  const context = vm.createContext({ __ATOLL_HOOK_EXE__: "missing", setTimeout, clearTimeout,
    spawn() { throw new Error("not installed"); } });
  vm.runInContext(source.replace(/^import .*$/m, "").replace("export const", "const")
    + "\nglobalThis.createPlugin = AtollPlugin;", context);
  const plugin = await context.createPlugin({ directory: "C:/project" });
  await plugin["tool.execute.before"]({ sessionID: "s1", tool: "read" }, { args: {} });
});
