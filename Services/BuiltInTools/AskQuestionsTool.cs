using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// VisualStudio_askQuestions 工具 — 向用户展示结构化问题并等待回答。
    /// 用于在规划阶段向用户澄清需求。
    /// </summary>
    public class AskQuestionsTool : BuiltInToolBase
    {
        public override string Name => "VisualStudio_askQuestions";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "VisualStudio_askQuestions",
                    Description = "向用户展示结构化问题并等待回答。用于在规划阶段向用户澄清需求。问题会以 UI 形式呈现给用户，用户回答后返回 JSON 格式的答案。",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            questions = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        header = new { type = "string", description = "问题简短标题（唯一标识）" },
                                        question = new { type = "string", description = "问题的完整描述文本" },
                                        options = new
                                        {
                                            type = "array",
                                            items = new
                                            {
                                                type = "object",
                                                properties = new
                                                {
                                                    label = new { type = "string", description = "选项文本" },
                                                    description = new { type = "string", description = "选项说明（可选）" }
                                                },
                                                required = new[] { "label" }
                                            },
                                            description = "可选选项列表，为空则允许自由文本输入"
                                        },
                                        multiSelect = new { type = "boolean", description = "是否允许多选，默认 false" },
                                        allowFreeformInput = new { type = "boolean", description = "除选项外是否允许自由文本输入，默认 true" }
                                    },
                                    required = new[] { "header", "question" }
                                },
                                description = "要向用户提问的问题列表（每次 1-2 个问题）"
                            }
                        },
                        required = new[] { "questions" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            return "💬 向用户提问";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;
            return "💬 用户已回答";
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            // 此工具的实际执行由 UI 层（DeepSeekChatControl）拦截处理，
            // 不在后端执行。如果到达此处，说明 UI 未拦截。
            return Task.FromResult("💬 问题已提交给用户，等待回答...");
        }
    }
}
