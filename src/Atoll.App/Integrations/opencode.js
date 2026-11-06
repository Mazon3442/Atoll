// ATOLL agent integration. Sends short activity summaries, never tool output or file contents.
import { spawn } from "node:child_process";

const hookExe = __ATOLL_HOOK_EXE__;

export const AtollPlugin = async ({ directory }) => {
  const active = new Set();
  const send = (event, sessionID, tool, args) => new Promise((resolve) => {
    if (!sessionID) return resolve();
    try {
      const child = spawn(hookExe, ["--agent", "opencode"], {
        windowsHide: true, stdio: ["pipe", "ignore", "ignore"],
      });
      const timer = setTimeout(() => { child.kill(); resolve(); }, 1000);
      child.on("error", () => { clearTimeout(timer); resolve(); });
      child.on("close", () => { clearTimeout(timer); resolve(); });
      child.stdin.on("error", () => {});
      const safe = {};
      for (const key of ["filePath", "file_path", "path", "description", "command", "pattern"])
        if (typeof args?.[key] === "string") safe[key] = args[key].slice(0, 300);
      child.stdin.end(JSON.stringify({
        hook_event_name: event, session_id: sessionID, cwd: directory,
        tool_name: tool, tool_input: safe,
      }));
    } catch { resolve(); }
  });
  const start = async (id) => {
    if (!id || active.has(id)) return;
    active.add(id);
    await send("UserPromptSubmit", id);
  };
  return {
    "tool.execute.before": async (input, output) => {
      await start(input.sessionID);
      await send("PreToolUse", input.sessionID, input.tool, output.args);
    },
    "tool.execute.after": async (input) => {
      await send("PostToolUse", input.sessionID, input.tool);
    },
    event: async ({ event }) => {
      const p = event.properties ?? {};
      const id = p.sessionID ?? p.info?.id;
      switch (event.type) {
        case "session.created": await send("SessionStart", id); break;
        case "session.status":
          if (p.status?.type === "busy") await start(id);
          // Idle status can precede session.idle; dedupe via the active set.
          else if (p.status?.type === "idle" && active.delete(id)) await send("Stop", id);
          break;
        case "session.idle": if (active.delete(id)) await send("Stop", id); break;
        case "session.deleted": active.delete(id); await send("SessionEnd", id); break;
        case "session.error": active.delete(id); await send("Error", id); break;
        case "permission.asked": await send("PermissionRequest", id); break;
        case "permission.replied": await send("PostToolUse", id); break;
      }
    },
  };
};
