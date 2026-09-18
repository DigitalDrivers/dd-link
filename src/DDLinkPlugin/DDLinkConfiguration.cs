using AssettoServer.Server.Configuration;
using FluentValidation;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace DDLinkPlugin;

// Written by race control for every race server: plugin_dd_link_cfg.yml
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class DDLinkConfiguration : IValidateConfiguration<DDLinkConfigurationValidator>
{
    [YamlMember(Description = "URL of the platform endpoint that receives the messages")]
    public string Endpoint { get; init; } = "";

    [YamlMember(Description = "Shared secret used to sign every message (HMAC-SHA256)")]
    public string Secret { get; init; } = "";

    [YamlMember(Description = "Id of the platform event this server runs")]
    public string EventId { get; init; } = "";

    [YamlMember(Description = "Name of this race server in the pool")]
    public string ServerId { get; init; } = "";

    [YamlMember(Description = "Directory for messages that are waiting for delivery")]
    public string SpoolDirectory { get; init; } = "dd-link-spool";

    [YamlMember(Description = "URL that receives the live state of the session (live timing). Empty: no live feed")]
    public string LiveEndpoint { get; init; } = "";

    [YamlMember(Description = "Milliseconds between two live states")]
    public int LiveIntervalMilliseconds { get; init; } = 1000;
}

public class DDLinkConfigurationValidator : AbstractValidator<DDLinkConfiguration>
{
    public DDLinkConfigurationValidator()
    {
        RuleFor(cfg => cfg.Endpoint)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .WithMessage("Endpoint must be an absolute http(s) URL");
        RuleFor(cfg => cfg.Secret).MinimumLength(32);
        RuleFor(cfg => cfg.EventId).NotEmpty();
        RuleFor(cfg => cfg.ServerId).NotEmpty();
        RuleFor(cfg => cfg.SpoolDirectory).NotEmpty();
        RuleFor(cfg => cfg.LiveEndpoint)
            .Must(url => url == "" || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            .WithMessage("LiveEndpoint must be empty or an absolute http(s) URL");
        RuleFor(cfg => cfg.LiveIntervalMilliseconds).InclusiveBetween(200, 10_000);
    }
}
