// ATOLL agent integration. Sends short activity summaries, never tool output or file contents.
import { Plugin } from "@opencode/plugin";
import { spawn } from "node:child_process";

const hookExe = __ATOLL_HOOK_EXE__;

export default Plugin.define({
  id: "atoll",
  async setup(ctx) {
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
          hook_event_name: event, session_id: sessionID, cwd: ctx.location?.directory ?? process.cwd(),
          tool_name: tool, tool_input: safe,
        }));
      } catch { resolve(); }
    });
    const start = async (id) => {
      if (!id || active.has(id)) return;
      active.add(id);
      await send("UserPromptSubmit", id);
    };

    const before = await ctx.tool.hook("execute.before", async (event) => {
      await start(event.sessionID);
      await send("PreToolUse", event.sessionID, event.tool, event.input);
    });

    const after = await ctx.tool.hook("execute.after", async (event) => {
      await send("PostToolUse", event.sessionID, event.tool);
    });

    const controller = new AbortController();
    const consuming = (async () => {
      for await (const event of ctx.event.subscribe({ signal: controller.signal })) {
        if (controller.signal.aborted) break;
        const p = event.data ?? {};
        const id = p.sessionID;
        switch (event.type) {
          case "session.created": await send("SessionStart", id); break;
          case "session.execution.started": await start(id); break;
          case "session.execution.succeeded": if (active.delete(id)) await send("Stop", id); break;
          case "session.deleted": active.delete(id); await send("SessionEnd", id); break;
          case "session.execution.failed": active.delete(id); await send("Error", id); break;
          case "session.execution.interrupted": active.delete(id); await send("Interrupt", id); break;
          case "permission.asked": await send("PermissionRequest", id); break;
          case "permission.replied": await send("PostToolUse", id); break;
        }
      }
    })().catch(() => {
      if (!controller.signal.aborted) console.warn("[atoll] OpenCode event subscription stopped unexpectedly.");
    });

    return async () => {
      controller.abort();
      await Promise.allSettled([before.dispose(), after.dispose(), consuming]);
    };
  },
});
