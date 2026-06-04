using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// 编辑 Healing 服务 — 当编辑匹配失败时，通过 AI 模型自动修正。
    /// 支持两级模型降级：降级模型（快速）→ 完整模型（强指令遵循）。
    /// 
    /// 参考: vscode-copilot-chat editFileHealing.tsx
    /// </summary>
    public class EditFileHealing
    {
        private readonly DeepSeekApiService _apiService;
        private readonly HealingConfig _config;

        public EditFileHealing(DeepSeekApiService apiService, HealingConfig? config = null)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
            _config = config ?? new HealingConfig();
        }

        /// <summary>
        /// 尝试 Healing 失败的编辑。
        /// 先使用降级模型，失败后使用完整模型重试。
        /// </summary>
        public async Task<HealingResponse?> HealAsync(HealingRequest request, CancellationToken ct)
        {
            if (!_config.Enabled) return null;

            var sw = Stopwatch.StartNew();

            // ── 第 1 次：降级模型 ──
            var response = await HealWithModelAsync(request, isFullModel: false, ct);
            if (response?.Success == true)
            {
                response.ModelUsed = _config.FallbackModelName ?? "fallback";
                response.ElapsedMs = sw.ElapsedMilliseconds;
                return response;
            }

            Logger.Warn(LocalizationService.Instance.Format("tool.edit.healing.degradedFailed", response?.ErrorMessage ?? "no response"));

            // ── 第 2 次：完整模型 ──
            var fullResponse = await HealWithModelAsync(request, isFullModel: true, ct);
            if (fullResponse?.Success == true)
            {
                fullResponse.ModelUsed = "full";
                fullResponse.ElapsedMs = sw.ElapsedMilliseconds;
                return fullResponse;
            }

            Logger.Warn(LocalizationService.Instance.Format("tool.edit.healing.fullFailed", fullResponse?.ErrorMessage ?? "cannot parse"));

            return new HealingResponse
            {
                Success = false,
                ErrorMessage = fullResponse?.ErrorMessage ?? LocalizationService.Instance["tool.edit.healing.completeFail"],
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }

        /// <summary>
        /// 使用指定模型进行单次 Healing 尝试。
        /// </summary>
        private async Task<HealingResponse?> HealWithModelAsync(
            HealingRequest request, bool isFullModel, CancellationToken ct)
        {
            try
            {
                var prompt = BuildHealingPrompt(request);
                string systemPrompt = isFullModel
                    ? LocalizationService.Instance["edit.healingFullSystemPrompt"]
                    : LocalizationService.Instance["edit.healingSystemPrompt"];

                string response = await _apiService.CompleteAsync(
                    new List<ChatApiMessage>
                    {
                        new ChatApiMessage { Role = "system", Content = systemPrompt },
                        new ChatApiMessage { Role = "user", Content = prompt },
                    },
                    ct);

                return ParseHealingResponse(response, request);
            }
            catch (Exception ex)
            {
                Logger.Warn(LocalizationService.Instance.Format("tool.edit.healing.exception", isFullModel ? LocalizationService.Instance["tool.edit.healing.full"] : LocalizationService.Instance["tool.edit.healing.degraded"], ex.Message));
                return new HealingResponse
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Instance.Format("tool.edit.healing.requestFailed", ex.Message),
                };
            }
        }

        /// <summary>
        /// 构建 Healing prompt。
        /// </summary>
        private static string BuildHealingPrompt(HealingRequest request)
        {
            var sb = new StringBuilder();
            var L = LocalizationService.Instance;

            sb.AppendLine(L["edit.healingHeaderCurrent"]);
            sb.AppendLine("```");
            sb.AppendLine(request.CurrentFileContent);
            sb.AppendLine("```");
            sb.AppendLine();

            sb.AppendLine(L["edit.healingHeaderFailed"]);
            sb.AppendLine($"{L["edit.operationTypeLabel"]}: {request.OriginalOperationType}");
            sb.AppendLine($"{L["edit.failureReasonLabel"]}: {request.FailureReason}");
            sb.AppendLine();

            switch (request.OriginalOperationType)
            {
                case EditOperationType.ApplyPatch when request.FailedPatch != null:
                    sb.AppendLine(L["edit.healingHeaderOriginalPatch"]);
                    sb.AppendLine("```");
                    sb.AppendLine(request.FailedPatch.RawText);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine(L["edit.healingInstructionPatch"]);
                    sb.AppendLine(L["edit.healingOutputFormat"]);
                    sb.AppendLine(L["edit.healingAdjustHint"]);
                    break;

                case EditOperationType.InsertEditIntoFile when request.FailedInsertEditContent != null:
                    sb.AppendLine(L["edit.healingHeaderOriginalInsert"]);
                    sb.AppendLine("```");
                    sb.AppendLine(request.FailedInsertEditContent);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine(L["edit.healingInstructionInsert"]);
                    sb.AppendLine(L["edit.healingOutputFormatInsert"]);
                    break;

                case EditOperationType.ApplyPatch when request.FailedReplaceInput != null:
                    // replace_string 的 healing: 要求重新输出 oldString/newString
                    sb.AppendLine(L["edit.healingHeaderOriginalReplace"]);
                    sb.AppendLine("```");
                    sb.AppendLine($"oldString:\n{request.FailedReplaceInput.OldString}");
                    sb.AppendLine($"newString:\n{request.FailedReplaceInput.NewString}");
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine(L["edit.healingInstructionReplace"]);
                    break;
            }

            return sb.ToString();
        }

        /// <summary>
        /// 解析 Healing 模型的响应。
        /// </summary>
        private HealingResponse? ParseHealingResponse(string response, HealingRequest request)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return new HealingResponse { Success = false, ErrorMessage = L["edit.healingEmptyResponse"] };
            }

            var result = new HealingResponse { Success = true };

            switch (request.OriginalOperationType)
            {
                case EditOperationType.ApplyPatch:
                    var patches = ApplyPatchTool.ParsePatches(response);
                    if (patches.Count > 0)
                    {
                        result.CorrectedPatch = patches[0];
                    }
                    else
                    {
                        result.Success = false;
                        result.ErrorMessage = L["edit.healingNoPatch"];
                    }
                    break;

                case EditOperationType.InsertEditIntoFile:
                    var codeBlockMatch = Regex.Match(response,
                        @"```(?:[\w#]*)?[\r\n]+(.*?)```", RegexOptions.Singleline);
                    result.CorrectedInsertEditContent = codeBlockMatch.Success
                        ? codeBlockMatch.Groups[1].Value
                        : response;
                    break;

                default:
                    // replace_string healing: 尝试从响应中提取修正后的 oldString/newString
                    result.Success = false;
                    result.ErrorMessage = LocalizationService.Instance["tool.edit.healing.unsupportedOp"];
                    break;
            }

            return result;
        }

        private static LocalizationService L => LocalizationService.Instance;
    }
}
