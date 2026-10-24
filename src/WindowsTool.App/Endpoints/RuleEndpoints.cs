using System.Text;
using System.Text.Json;
using WindowsTool.App.Common;
using WindowsTool.App.Rules;
using WindowsTool.App.Scans;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Storage;

namespace WindowsTool.App.Endpoints;

/// <summary>規則管理 API（PLAN 11.3）。寫入前一律經過 <see cref="RuleValidator"/>。</summary>
public static class RuleEndpoints
{
    public static IEndpointRouteBuilder MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/api/rules");
        rules.MapGet("/", List);
        rules.MapPost("/validate", ValidateAsync);
        rules.MapPost("/", CreateAsync);
        rules.MapPut("/{id}", UpdateAsync);
        rules.MapDelete("/{id}", Delete);
        rules.MapPut("/{id}/enabled", SetEnabled);
        rules.MapGet("/export", Export);
        rules.MapPost("/import", ImportAsync);
        rules.MapPost("/reload", Reload);
        return app;
    }

    /// <param name="source">builtin / user / ai，省略時列出全部。</param>
    private static IResult List(string? source, RuleStore store, AppPaths paths)
    {
        RuleSource? filter = source?.ToLowerInvariant() switch
        {
            null or "" => null,
            "builtin" => RuleSource.Builtin,
            "user" => RuleSource.User,
            "ai" => RuleSource.Ai,
            _ => (RuleSource)(-1),
        };

        if (filter == (RuleSource)(-1))
        {
            return TypedResults.BadRequest(new ErrorResponse($"不支援的 source：{source}"));
        }

        return TypedResults.Ok(BuildList(store.Current, paths, filter));
    }

    private static async Task<IResult> ValidateAsync(HttpRequest http, RuleStore store, ScanManager scans)
    {
        var (request, error) = await ReadRequestAsync(http).ConfigureAwait(false);
        if (request?.Rule is null)
        {
            return TypedResults.BadRequest(new ErrorResponse(error ?? "請提供要驗證的規則。"));
        }

        var existing = store.Current.User.FirstOrDefault(r => r.Id == request.Rule.Id);
        var rule = Prepare(request.Rule, existing, store);
        return TypedResults.Ok(Validate(rule, request, store, scans));
    }

    private static async Task<IResult> CreateAsync(HttpRequest http, RuleStore store, ScanManager scans)
    {
        var (request, error) = await ReadRequestAsync(http).ConfigureAwait(false);
        if (request?.Rule is null)
        {
            return TypedResults.BadRequest(new ErrorResponse(error ?? "請提供要新增的規則。"));
        }

        var rule = Prepare(request.Rule, null, store);
        return Save(rule, request, store, scans, s => s.Add(rule), created: true);
    }

    private static async Task<IResult> UpdateAsync(string id, HttpRequest http, RuleStore store, ScanManager scans)
    {
        var (request, error) = await ReadRequestAsync(http).ConfigureAwait(false);
        if (request?.Rule is null)
        {
            return TypedResults.BadRequest(new ErrorResponse(error ?? "請提供修改後的規則。"));
        }

        var snapshot = store.Current;
        var existing = snapshot.User.FirstOrDefault(r => r.Id == id);
        if (existing is null)
        {
            return snapshot.Builtin.Any(r => r.Id == id)
                ? TypedResults.BadRequest(new ErrorResponse("內建規則不能修改；可以複製成自訂規則後再修改。"))
                : NotFound(id);
        }

        var rule = Prepare(request.Rule with { Id = string.IsNullOrWhiteSpace(request.Rule.Id) ? id : request.Rule.Id }, existing, store);
        return Save(rule, request, store, scans, s => s.Update(id, rule), created: false);
    }

    private static IResult Delete(string id, RuleStore store)
    {
        try
        {
            store.Delete(id);
            return TypedResults.NoContent();
        }
        catch (RuleStoreException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(id);
        }
    }

    private static IResult SetEnabled(string id, EnabledRequest request, RuleStore store)
    {
        try
        {
            var snapshot = store.SetEnabled(id, request.Enabled);
            return TypedResults.Ok(Info(snapshot.Find(id)!, snapshot));
        }
        catch (RuleStoreException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(id);
        }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult Export(RuleStore store) =>
        TypedResults.File(Encoding.UTF8.GetBytes(store.Export()), "application/json; charset=utf-8", "rules.user.json");

    /// <summary>匯入 rules.user.json 格式的檔案：全部通過驗證才寫入（全有或全無）。</summary>
    private static async Task<IResult> ImportAsync(HttpRequest http, RuleStore store)
    {
        string body;
        using (var reader = new StreamReader(http.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(http.HttpContext.RequestAborted).ConfigureAwait(false);
        }

        RuleFile file;
        try
        {
            file = RuleJson.ParseFile(body);
        }
        catch (JsonException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse($"檔案格式錯誤：{ex.Message}"));
        }

        var snapshot = store.Current;
        var problems = new List<ImportProblem>();
        var prepared = new List<Rule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var original in file.Rules)
        {
            var rule = Prepare(original with { Meta = original.Meta }, null, store) with
            {
                Id = string.IsNullOrWhiteSpace(original.Id) ? "" : original.Id.Trim(),
            };

            var result = RuleValidator.Validate(rule, new RuleValidationContext
            {
                Current = snapshot.Set,
                Probe = new FileSystemEntryProbe(),
                AllowGlobalScopeForAi = true, // 使用者自己匯入的檔案
            });

            var issues = result.Issues.Where(i => i.IsError).ToList();
            if (snapshot.Find(rule.Id) is not null || !ids.Add(rule.Id))
            {
                issues.Add(RuleIssue.Error("id", $"已經有 id 為「{rule.Id}」的規則。"));
            }

            if (issues.Count > 0)
            {
                problems.Add(new ImportProblem(rule.Id, issues));
            }

            prepared.Add(rule);
        }

        if (problems.Count > 0)
        {
            return TypedResults.BadRequest(new ImportResponse(0, 0, problems));
        }

        try
        {
            if (prepared.Count > 0)
            {
                store.AddRange(prepared);
            }

            var disabled = 0;
            foreach (var id in file.DisabledBuiltin ?? [])
            {
                if (store.Current.Builtin.FirstOrDefault(r => r.Id == id) is { Kind: not RuleKind.Protect, Enabled: true })
                {
                    store.SetEnabled(id, false);
                    disabled++;
                }
            }

            return TypedResults.Ok(new ImportResponse(prepared.Count, disabled, []));
        }
        catch (RuleStoreException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<RulesResponse> Reload(RuleStore store, AppPaths paths) => TypedResults.Ok(BuildList(store.Reload(), paths, null));

    // ---- 共用 ----------------------------------------------------------------

    private static IResult Save(
        Rule rule, RuleRequest request, RuleStore store, ScanManager scans, Func<RuleStore, RuleSnapshot> write, bool created)
    {
        var validation = Validate(rule, request, store, scans);
        if (!validation.Valid)
        {
            return TypedResults.BadRequest(validation);
        }

        try
        {
            var snapshot = write(store);
            var response = new RuleSavedResponse(snapshot.Version, Info(snapshot.Find(rule.Id)!, snapshot), validation);
            return created ? TypedResults.Created($"/api/rules/{rule.Id}", response) : TypedResults.Ok(response);
        }
        catch (RuleStoreException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static ValidationResponse Validate(Rule rule, RuleRequest request, RuleStore store, ScanManager scans)
    {
        var snapshot = store.Current;
        var session = string.IsNullOrWhiteSpace(request.ScanId) ? null : scans.Get(request.ScanId);
        var scan = session is { Status: ScanStatus.Completed } ? session.Result : null;
        var evaluation = scan is null ? null : session!.GetEvaluation(snapshot);

        var result = RuleValidator.Validate(rule, new RuleValidationContext
        {
            Current = snapshot.Set,
            Scan = scan,
            Evaluation = evaluation,
            Probe = new FileSystemEntryProbe(),
            AllowGlobalScopeForAi = request.AllowGlobalScope ?? false,
        });

        var issues = result.Issues.ToList();
        if (!string.IsNullOrWhiteSpace(request.ScanId) && scan is null)
        {
            issues.Add(RuleIssue.Warning("scanId", "找不到已完成的掃描結果，只做了格式與保護路徑的檢查（沒有試套用）。"));
        }

        return new ValidationResponse(result.IsValid, rule, issues, result.DryRun, scan?.Root.Name);
    }

    /// <summary>補上伺服器端決定的欄位：id、來源（使用者送來的一律是 user，AI 提案保留 ai）、建立 / 修改時間。</summary>
    private static Rule Prepare(Rule rule, Rule? existing, RuleStore store)
    {
        var now = DateTime.UtcNow;
        var meta = rule.Meta ?? new RuleMeta();
        var id = string.IsNullOrWhiteSpace(rule.Id) ? existing?.Id ?? store.NextUserId() : rule.Id.Trim();

        return rule with
        {
            Id = id,
            Name = rule.Name?.Trim() ?? "",
            Source = rule.Source == RuleSource.Ai ? RuleSource.Ai : RuleSource.User,
            Meta = meta with
            {
                CreatedUtc = existing?.Meta?.CreatedUtc ?? meta.CreatedUtc ?? now,
                UpdatedUtc = existing is null ? meta.UpdatedUtc : now,
                ApprovedBy = "user",
                HitCount = existing?.Meta?.HitCount ?? meta.HitCount,
                LastHitUtc = existing?.Meta?.LastHitUtc ?? meta.LastHitUtc,
            },
        };
    }

    private static RulesResponse BuildList(RuleSnapshot snapshot, AppPaths paths, RuleSource? filter) => new(
        snapshot.Version,
        paths.UserRulesFile,
        paths.IsPortable,
        snapshot.Set.Platform,
        snapshot.LoadError,
        snapshot.LoadWarnings,
        [.. snapshot.All.Where(r => filter is null || r.Source == filter).Select(r => Info(r, snapshot))],
        CommandCatalog.All,
        snapshot.Set.Tokens.Values);

    private static RuleInfoDto Info(Rule rule, RuleSnapshot snapshot) => new(
        rule,
        snapshot.Set.IsActive(rule.Id),
        Editable: rule.Source != RuleSource.Builtin,
        CanDisable: !(rule.Source == RuleSource.Builtin && rule.Kind == RuleKind.Protect),
        snapshot.Set.Issues.TryGetValue(rule.Id, out var issues) ? issues : []);

    /// <summary>自己讀取 body：JSON 格式錯誤（例如打錯欄位名稱）時回傳看得懂的訊息。</summary>
    private static async Task<(RuleRequest? Request, string? Error)> ReadRequestAsync(HttpRequest http)
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync(http.Body, AppJsonContext.Default.RuleRequest, http.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            return (request, null);
        }
        catch (JsonException ex)
        {
            return (null, $"規則格式錯誤：{ex.Message}");
        }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.NotFound<ErrorResponse> NotFound(string id) => TypedResults.NotFound(new ErrorResponse($"找不到規則：{id}"));
}
