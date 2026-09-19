using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace DDLinkPlugin;

public class DDLinkModule : AssettoServerModule<DDLinkConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<SpectatorSlots>().AsSelf().SingleInstance();
        builder.RegisterType<DDLinkService>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<DDLinkLiveService>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<DDLinkBanService>().AsSelf().As<IHostedService>().SingleInstance();
    }
}
