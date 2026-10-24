using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsTool.App.Endpoints;

namespace WindowsTool.App;

/// <summary>
/// JSON source generator：API 用到的型別都要列在這裡，之後開啟 trimming 才不會出問題。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SystemInfoResponse))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
