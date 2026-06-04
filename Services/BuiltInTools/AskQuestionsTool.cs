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
                    Description = LocalizationService.Instance["tool.askQuestions.description"],
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
                                        header = new { type = "string", description = LocalizationService.Instance["tool.askQuestions.param.header"] },
                                        question = new { type = "string", description = LocalizationService.Instance["tool.askQuestions.param.question"] },
                                        options = new
                                        {
                                            type = "array",
                                            items = new
                                            {
                                                type = "object",
                                                properties = new
                                                {
                                                    label = new { type = "string", description = LocalizationService.Instance["tool.askQuestions.param.optionLabel"] },
                                                    description = new { type = "string", description = LocalizationService.Instance["tool.askQuestions.param.optionDescription"] }
                                                },
                                                required = new[] { "label" }
                                            },
                                            description = LocalizationService.Instance["tool.askQuestions.param.options"]
                                        },
                                        multiSelect = new { type = "boolean", description = LocalizationService.Instance["tool.askQuestions.param.multiSelect"] },
                                        allowFreeformInput = new { type = "boolean", description = LocalizationService.Instance["tool.askQuestions.param.allowFreeformInput"] }
                                    },
                                    required = new[] { "header", "question" }
                                },
                                description = LocalizationService.Instance["tool.askQuestions.param.questions"]
                            }
                        },
                        required = new[] { "questions" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            return LocalizationService.Instance["tool.askQuestions.displayText"];
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            return LocalizationService.Instance["tool.askQuestions.answered"];
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            // 此工具的实际执行由 UI 层（DeepSeekChatControl）拦截处理，
            // 不在后端执行。如果到达此处，说明 UI 未拦截。
            return Task.FromResult(LocalizationService.Instance["tool.askQuestions.submitted"]);
        }
    }
}
