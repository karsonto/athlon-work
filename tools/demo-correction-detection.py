"""
Demonstrate why CorrectionDetector couldn't detect tool failures.

Shows the actual tool result format vs what the old code was looking for.
Run: python tools/demo-correction-detection.py
"""

def main():
    # 真实的工具失败结果格式 (ModelMessageBuilder.FormatToolResult)
    actual_failure = """ToolCallId: call_abc123
Tool `file_read` failed.

Arguments: {"path": "nonexistent-file.txt"}
Summary: File not found"""

    print("=" * 72)
    print("实际失败的工具结果 (FormatToolResult 输出)")
    print("=" * 72)
    print(actual_failure)
    print()

    # ========== 旧逻辑 ==========
    print("-" * 72)
    print("旧检测逻辑: content.StartsWith(\"Error:\")")
    print("-" * 72)

    def old_check(content):
        if content.startswith("Error:"):
            return "✅ 检测到失败"
        return "❌ 未检测到失败"

    print(f"  结果: {old_check(actual_failure)}")
    print(f"  原因: 实际内容以 'ToolCallId:' 开头，不是 'Error:'")
    print()

    # ========== 新逻辑 ==========
    print("-" * 72)
    print("新检测逻辑: contains(\"failed.\") AND contains(\"Tool `\")")
    print("-" * 72)

    def new_check(content):
        if not content or not content.strip():
            return "✅ (空视为失败)"
        if "failed." in content.lower() and "Tool `" in content:
            return "✅ 检测到失败"
        if content.startswith("Error:"):
            return "✅ 检测到失败"
        return "❌ 未检测到失败"

    print(f"  结果: {new_check(actual_failure)}")
    print(f"  原因: 内容包含 \"Tool `file_read` failed.\"")
    print()

    # ========== 边界测试 ==========
    print("=" * 72)
    print("其他边界测试")
    print("=" * 72)

    test_cases = [
        ("空内容", "", "✅ (空视为失败)"),
        ("成功结果", """ToolCallId: call_xyz
Tool `file_list` succeeded.

Arguments: {}
Summary: Listed 15 entries
src/
docs/""", "❌ (succeeded, 不是 failed)"),
        ("超时结果", """ToolCallId: call_timeout
Tool `grep_files` failed.

Arguments: {"pattern": "test"}
Summary: Operation timed out""", "✅ (包含 failed.)"),
        ("Error: 格式", "Error: File not found", "✅ (兼容旧格式)"),
    ]

    for name, content, expected in test_cases:
        result = new_check(content)
        status = "✅ 匹配预期" if result == expected[:2] else "⚠️ 不符预期"
        print(f"  [{name}]")
        print(f"    内容: {repr(content[:60])}...")
        print(f"    结果: {result}")
        print(f"    预期: {expected}")
        print()

    # ========== 完整检测管线演示 ==========
    print("=" * 72)
    print("完整修正轨迹检测管线")
    print("=" * 72)

    # 模拟一段对话: user → assistant(tool_call: file_read) → tool(failed) → user(correction) → assistant(tool_call: file_read) → tool(success)
    assistant_with_call = {
        "role": "assistant",
        "tool_calls": '[{"id":"call_1","function":{"name":"file_read","arguments":"{\\\"path\\\":\\\"nonexistent.txt\\\"}"}}]'
    }
    failed_tool = actual_failure
    user_correction = "路径写错了，读 README.md 吧"
    assistant_retry = {
        "role": "assistant",
        "tool_calls": '[{"id":"call_2","function":{"name":"file_read","arguments":"{\\\"path\\\":\\\"README.md\\\"}"}}]'
    }
    success_tool = """ToolCallId: call_2
Tool `file_read` succeeded.

Arguments: {"path": "README.md"}
Summary: Read 337 lines
# Athlon Agent
..."""

    print(f"  Assistant #1 发出 tool_call: file_read(\"nonexistent.txt\")")
    print(f"  Tool result:   {failed_tool.split(chr(10))[1]}")
    print(f"  IsFailedToolResult → ✅ 匹配!")
    print()
    print(f"  User #2:      \"{user_correction}\"")
    print(f"  → FindNextUserMessage: 找到修正指令 ✅")
    print()
    print(f"  Assistant #3 发出 tool_call: file_read(\"README.md\")")
    print(f"  Tool result:   {success_tool.split(chr(10))[1]}")
    print(f"  IsFailedToolResult → ✅ 不匹配(非失败)，视为成功")
    print()
    print(f"  → FindSuccessfulRetry: 找到同名(file_read)成功调用 ✅")
    print()
    print(f"  🎉 生成 CorrectionTrajectory:")
    print(f"     FailedToolCall:      file_read(\"nonexistent.txt\")")
    print(f"     CorrectionMessage:  \"路径写错了，读 README.md 吧\"")
    print(f"     SuccessfulToolCall:  file_read(\"README.md\")")
    print()
    print(f"  → TrainingSampleStore 写入 sft-traces-*.jsonl ✅")


if __name__ == "__main__":
    main()
