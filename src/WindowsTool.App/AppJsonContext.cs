using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsTool.App.Endpoints;
using WindowsTool.App.Common;
using WindowsTool.App.Plans;
using WindowsTool.App.Rules;
using WindowsTool.Core.Execution;
using WindowsTool.App.Scans;
using WindowsTool.App.Search;

namespace WindowsTool.App;

/// <summary>
/// JSON source generator：API 用到的型別都要列在這裡，之後開啟 trimming 才不會出問題。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SystemInfoResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(StartScanRequest))]
[JsonSerializable(typeof(StartScanResponse))]
[JsonSerializable(typeof(ScanStatusDto))]
[JsonSerializable(typeof(ItemPageDto))]
[JsonSerializable(typeof(ItemDetailDto))]
[JsonSerializable(typeof(StartSearchRequest))]
[JsonSerializable(typeof(StartSearchResponse))]
[JsonSerializable(typeof(SearchEventDto))]
[JsonSerializable(typeof(ShellTargetRequest))]
[JsonSerializable(typeof(CategoriesDto))]
[JsonSerializable(typeof(SelectionDto))]
[JsonSerializable(typeof(SelectionRequest))]
[JsonSerializable(typeof(RuleRequest))]
[JsonSerializable(typeof(EnabledRequest))]
[JsonSerializable(typeof(RuleInfoDto))]
[JsonSerializable(typeof(RulesResponse))]
[JsonSerializable(typeof(ValidationResponse))]
[JsonSerializable(typeof(RuleSavedResponse))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(CreatePlanRequest))]
[JsonSerializable(typeof(ExecuteRequest))]
[JsonSerializable(typeof(PlanResponse))]
[JsonSerializable(typeof(LogsResponse))]
[JsonSerializable(typeof(QuarantineResponse))]
[JsonSerializable(typeof(QuarantineEntry))]
[JsonSerializable(typeof(PurgeRequest))]
[JsonSerializable(typeof(PurgeResult))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
