using Microsoft.Extensions.Configuration;
using SmartSolar.Infrastructure.Features;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Tests.Unit.Common;

/// <summary>Feature flags of the Installation Flow and the knowledge base, including the deprecated Features:Flow2Enabled.</summary>
public sealed class FeatureOptionsTests
{
    private static FeatureOptions Read(params (string Key, string Value)[] values)
        => FeatureConfiguration.Read(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void Both_features_are_off_by_default()
    {
        var options = Read();
        Assert.False(options.InstallationWorkflowEnabled);
        Assert.False(options.KnowledgeBaseEnabled);
        Assert.False(options.UsesLegacyFlag);
    }

    [Fact]
    public void New_keys_enable_each_feature_independently()
    {
        var options = Read(("Features:InstallationWorkflowEnabled", "true"));
        Assert.True(options.InstallationWorkflowEnabled);
        Assert.False(options.KnowledgeBaseEnabled);
        Assert.True(options.IsEnabled(FeatureOptions.InstallationWorkflow));
        Assert.False(options.IsEnabled(FeatureOptions.KnowledgeBase));
        Assert.False(options.IsEnabled("UNKNOWN"));
    }

    [Fact]
    public void The_deprecated_flag_enables_both_features_and_is_reported()
    {
        var options = Read(("Features:Flow2Enabled", "true"));
        Assert.True(options.InstallationWorkflowEnabled);
        Assert.True(options.KnowledgeBaseEnabled);
        Assert.True(options.UsesLegacyFlag);
    }

    [Fact]
    public void Explicit_new_keys_win_over_the_deprecated_flag()
    {
        var options = Read(("Features:Flow2Enabled", "true"), ("Features:KnowledgeBaseEnabled", "false"));
        Assert.True(options.InstallationWorkflowEnabled);
        Assert.False(options.KnowledgeBaseEnabled);

        var both = Read(("Features:Flow2Enabled", "false"), ("Features:InstallationWorkflowEnabled", "true"), ("Features:KnowledgeBaseEnabled", "true"));
        Assert.True(both.InstallationWorkflowEnabled && both.KnowledgeBaseEnabled);
        Assert.False(both.UsesLegacyFlag);
    }
}
