# ATOLL agent integration. Observer callbacks always return None and never expose tool output.
import json
import os
import subprocess

HOOK_EXE = __ATOLL_HOOK_EXE__


def _send(event, session_id="", tool_name=None, args=None, **kwargs):
    session_id = session_id or kwargs.get("session_key") or kwargs.get("task_id")
    if not session_id:
        return
    safe = {k: v[:300] for k, v in (args if isinstance(args, dict) else {}).items()
            if k in ("file_path", "filePath", "path", "command", "cmd", "description", "pattern")
            and isinstance(v, str)}
    payload = {"hook_event_name": event, "session_id": session_id, "cwd": os.getcwd(),
               "tool_name": tool_name, "tool_input": safe}
    try:
        subprocess.run([HOOK_EXE, "--agent", "hermes"], input=json.dumps(payload).encode("utf-8"),
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=1,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), check=False)
    except (OSError, subprocess.TimeoutExpired):
        pass


def _ended(completed=False, interrupted=False, **kwargs):
    if interrupted:
        _send("Interrupt", **kwargs)
    elif not completed:
        _send("Error", **kwargs)


def register(ctx):
    for hook, event in (
        ("on_session_start", "SessionStart"),
        ("pre_llm_call", "UserPromptSubmit"),
        ("pre_tool_call", "PreToolUse"),
        ("post_tool_call", "PostToolUse"),
        ("post_llm_call", "Stop"),
        ("on_human_input_request", "PermissionRequest"),
        ("on_human_input_resolved", "PostToolUse"),
    ):
        ctx.register_hook(hook, lambda _event=event, **kwargs: _send(_event, **kwargs))
    ctx.register_hook("on_session_end", _ended)
