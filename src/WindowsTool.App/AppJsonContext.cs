using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsTool.App.Endpoints;
using WindowsTool.App.Scans;

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
internal sealed partial class AppJsonContext : JsonSerializerContext;
