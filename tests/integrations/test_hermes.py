import json
from pathlib import Path
import unittest
from unittest.mock import patch


class HermesAdapterTests(unittest.TestCase):
    def setUp(self):
        source = Path(__file__).resolve().parents[2] / "src/Atoll.App/Integrations/hermes.py"
        self.module = {"__ATOLL_HOOK_EXE__": "C:/ATOLL/Atoll.ClaudeHook.exe"}
        exec(compile(source.read_text(encoding="utf-8"), str(source), "exec"), self.module)
        self.hooks = {}

        class Context:
            def register_hook(_, name, callback):
                self.hooks[name] = callback
        self.module["register"](Context())

    def test_turn_and_permission_events_are_passive_and_private(self):
        with patch.object(self.module["subprocess"], "run") as run:
            for hook in ("on_session_start", "pre_llm_call", "pre_tool_call", "on_human_input_request",
                         "on_human_input_resolved", "post_tool_call", "post_llm_call"):
                self.assertIsNone(self.hooks[hook](session_id="s1", tool_name="write_file",
                    args={"file_path": "app.py", "content": "secret contents"}, result="secret output"))
            messages = [json.loads(call.kwargs["input"]) for call in run.call_args_list]
            self.assertEqual([m["hook_event_name"] for m in messages],
                ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "PostToolUse", "Stop"])
            self.assertNotIn("secret", json.dumps(messages))
            self.assertEqual(run.call_args.args[0][1:], ["--agent", "hermes"])

    def test_interrupted_or_failed_turns_never_report_success(self):
        with patch.object(self.module["subprocess"], "run") as run:
            self.hooks["on_session_end"](session_id="s1", interrupted=True)
            self.hooks["on_session_end"](session_id="s1", completed=False)
            self.hooks["on_session_end"](session_id="s1", completed=True)
            self.assertEqual([json.loads(c.kwargs["input"])["hook_event_name"] for c in run.call_args_list],
                             ["Interrupt", "Error"])

    def test_closed_island_cannot_break_a_hermes_turn(self):
        with patch.object(self.module["subprocess"], "run", side_effect=OSError("missing exe")):
            self.assertIsNone(self.hooks["pre_llm_call"](session_id="s1"))


if __name__ == "__main__":
    unittest.main()
