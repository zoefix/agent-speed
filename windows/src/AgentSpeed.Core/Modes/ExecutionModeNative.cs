namespace AgentSpeed.Core;

public static class ExecutionModeNative
{
    public static ExecutionModes ClaudeSpeed(string? speed) => ExecutionModes.Of(
        speed is "fast" or "standard" ? ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, speed,
            ExecutionModeEvidence.Served, "anthropic") : null);

    public static ExecutionModes KimiModel(string? model) => ExecutionModes.Of(
        model is "k3.1-pro-highspeed" or "kimi-for-coding-highspeed"
            ? new(ExecutionModeKind.ModelVariant, "highspeed", model, ExecutionModeEvidence.Selected, "kimi", ExecutionModeScope.Request)
            : null);

    public static ExecutionModes Routing(string? model) => ExecutionModes.Of(
        model is "fast-model" or "balanced-model" or "deep-model" or "auto"
            ? new(ExecutionModeKind.Routing, model == "auto" ? model : model[..^6], model, ExecutionModeEvidence.Selected, Scope: ExecutionModeScope.Request)
            : null);

    public static ExecutionModes OpenAiTier(string? tier, ExecutionModeEvidence evidence = ExecutionModeEvidence.Served,
        ExecutionModeScope scope = ExecutionModeScope.Response, string? provider = "openai") =>
        provider is "openai" or "azure"
            ? ExecutionModes.Of(ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, tier, evidence, "openai", scope))
            : ExecutionModes.Empty;

    /// <summary>Google: <c>priority</c> is <c>fast</c>, <c>standard</c> and <c>flex</c> as written, <c>unspecified</c> no fact.</summary>
    public static ExecutionModes GoogleTier(string? tier, ExecutionModeEvidence evidence, ExecutionModeScope scope) => ExecutionModes.Of(
        string.Equals(tier, "unspecified", StringComparison.OrdinalIgnoreCase) ? null
            : ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, tier, evidence, "google", scope));

    /// <summary>
    /// The <c>effort</c> adapter: <c>none</c> and <c>disabled</c> are <c>off</c>, <c>enabled</c> is <c>on</c>; unresolved tokens
    /// give no fact; others stay distinct.
    /// </summary>
    public static ExecutionModeFact? Effort(string? raw, ExecutionModeEvidence evidence, ExecutionModeScope scope) =>
        ExecutionModeFact.Create(ExecutionModeKind.Effort, raw, evidence, scope: scope) is { } fact
            ? fact.Value switch
            {
                "auto" or "default" or "inherit" or "inherited" or "unknown" or "thinking_level_unspecified" => null,
                "none" or "disabled" => fact with { Value = "off" },
                "enabled" => fact with { Value = "on" },
                _ => fact,
            }
            : null;

    public static ExecutionModes Workflow(string? workflow) => ExecutionModes.Of(workflow is "fast" or "planning"
        ? ExecutionModeFact.Create(ExecutionModeKind.Workflow, workflow, ExecutionModeEvidence.Selected, scope: ExecutionModeScope.Turn)
        : null);

    /// <summary>The omp family of a provider; null when it has none, which gives no tier fact.</summary>
    public static string? OmpFamily(string? provider) => provider switch
    {
        "anthropic" => "anthropic",
        "openai" or "openai-codex" or "azure" or "azure-openai-responses" => "openai",
        "google" or "google-gemini-cli" or "google-antigravity" or "google-vertex" => "google",
        _ => null,
    };

    /// <summary>The <c>ompTier</c> adapter: <c>priority</c> is <c>fast</c> in every family, <c>default</c> is <c>standard</c> for OpenAI.</summary>
    public static ExecutionModes OmpTier(string? tier, string? provider, ExecutionModeEvidence evidence, ExecutionModeScope scope)
    {
        if (OmpFamily(provider) is not { } family) return ExecutionModes.Empty;
        var fact = ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, tier, evidence, family, scope);
        return ExecutionModes.Of(fact is { Value: "priority" } ? fact with { Value = "fast" } : fact);
    }

    /// <summary>The <c>zcodeModel</c> adapter: exact catalog ids, no substring matching.</summary>
    public static ExecutionModes ZCodeModel(string? model) => ExecutionModes.Of(
        model is "GLM-5.1-Highspeed" or "MiniMax-M2.7-highspeed" or "MiniMax-M2.5-highspeed" or "kimi-k2.7-code-highspeed"
            or "kimi-for-coding-highspeed" or "kimi-k3.1-pro-highspeed" or "k3.1-pro-highspeed"
            ? new(ExecutionModeKind.ModelVariant, "highspeed", model, ExecutionModeEvidence.Selected, "zcode", ExecutionModeScope.Request)
            : null);

    /// <summary>The <c>zcodeProvider</c> adapter: an attempt that ran on an acceleration card.</summary>
    public static ExecutionModes ZCodeProvider(string? provider) => ExecutionModes.Of(
        provider is "account:zai-highspeed-card" or "account:bigmodel-highspeed-card"
            ? new(ExecutionModeKind.Accelerator, "highspeed", provider, ExecutionModeEvidence.Recorded, "zcode", ExecutionModeScope.Request)
            : null);

    /// <summary>The <c>droidModel</c> adapter: Factory's catalog ids that change the request's tier, matched exactly.</summary>
    public static ExecutionModes DroidModel(string? model)
    {
        (string Value, string Provider)? tier = model switch
        {
            "claude-opus-4-6-fast" or "claude-opus-4-7-fast" or "claude-opus-4-8-fast" or "claude-opus-5-fast" or "claude-opus-5-5-fast"
                => ("fast", "anthropic"),
            "gpt-5.3-codex-fast" or "gpt-5.4-fast" or "gpt-5.4-mini-fast" or "gpt-5.5-fast" or "gpt-5.6-fast" or "gpt-5.6-sol-fast"
                or "gpt-6-sol-fast" or "gpt-6-luna-fast" => ("fast", "openai"),
            "gpt-6-astra-ultrafast" or "gpt-6.1-sol-ultrafast" => ("ultrafast", "openai"),
            "gpt-5.6-terra-flex" or "gpt-5.6-luna-flex" or "gpt-6-sol-flex" or "gpt-6-astra-flex" or "gpt-6-luna-flex" => ("flex", "openai"),
            _ => null,
        };
        return ExecutionModes.Of(tier is { } known
            ? new(ExecutionModeKind.ServiceTier, known.Value, model!, ExecutionModeEvidence.Recorded, known.Provider, ExecutionModeScope.Request)
            : null);
    }

    /// <summary>The <c>hermesEffort</c> adapter: anything but Hermes's own levels gives no fact, as Hermes ignores it too.</summary>
    public static ExecutionModes HermesEffort(string? raw, ExecutionModeEvidence evidence, ExecutionModeScope scope)
    {
        var fact = ExecutionModeFact.Create(ExecutionModeKind.Effort, raw, evidence, scope: scope);
        return ExecutionModes.Of(fact?.Value switch
        {
            "none" or "false" or "disabled" => fact with { Value = "off" },
            "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra" => fact,
            _ => null,
        });
    }

    /// <summary>The <c>hermesServiceTier</c> adapter; <c>auto</c> and <c>cold</c> open bounded windows and stay unresolved.</summary>
    public static ExecutionModes HermesServiceTier(string? raw, ExecutionModeEvidence evidence, ExecutionModeScope scope)
    {
        var fact = ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, raw, evidence, scope: scope);
        var value = fact?.Value switch
        {
            "fast" or "priority" => "fast",
            "ultrafast" or "ultra" => "ultrafast",
            "normal" or "default" or "standard" or "off" or "none" => "standard",
            _ => null,
        };
        return ExecutionModes.Of(value is null ? null : fact! with { Value = value });
    }

    /// <summary>The exact <c>(Low)</c>, <c>(Medium)</c>, <c>(High)</c> or <c>(Max)</c> end of a display name after a non-empty name.</summary>
    public static string? AntigravityLabelEffort(string label)
    {
        foreach (var word in (ReadOnlySpan<string>)["Low", "Medium", "High", "Max"])
        {
            if (label.Length > word.Length + 3 && label.EndsWith($" ({word})", StringComparison.Ordinal)) return word;
        }

        return null;
    }

    /// <summary>The base model and effort token of a catalog id that ends in exactly one effort suffix, else null.</summary>
    public static (string Base, string Effort)? AntigravityModelEffort(string model)
    {
        foreach (var suffix in (ReadOnlySpan<string>)["-extra-low", "-low", "-medium", "-high", "-max"])
        {
            if (model.Length > suffix.Length && model.EndsWith(suffix, StringComparison.Ordinal)) return (model[..^suffix.Length], suffix[1..]);
        }

        return null;
    }
}
