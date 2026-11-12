using System.Reflection;
using Renci.SshNet;

namespace CloudDriveMount.Core.Providers;

/// <summary>
/// Intercepts SSH.NET session channel creation to elevate SFTP sessions into root
/// superuser mode by issuing an SSH 'exec' request with sudo rather than a subsystem request.
/// </summary>
public static class SftpSudoInterop
{
    private static readonly MethodInfo DispatchProxyCreateMethod = typeof(DispatchProxy).GetMethods()
        .First(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2);

    private static readonly Type ServiceFactoryInterfaceType =
        typeof(SftpClient).Assembly.GetType("Renci.SshNet.IServiceFactory")!;

    private static readonly Type ServiceFactoryConcreteType =
        typeof(SftpClient).Assembly.GetType("Renci.SshNet.ServiceFactory")!;

    private static readonly Type SessionInterfaceType =
        typeof(SftpClient).Assembly.GetType("Renci.SshNet.ISession")!;

    private static readonly Type ChannelSessionInterfaceType =
        typeof(SftpClient).Assembly.GetType("Renci.SshNet.Channels.IChannelSession")!;

    private static readonly ConstructorInfo SftpClientInternalCtor =
        typeof(SftpClient).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(ConnectionInfo), typeof(bool), ServiceFactoryInterfaceType },
            null)!;

    public static readonly string[] DefaultSudoCandidates = new[]
    {
        "sudo /usr/lib/openssh/sftp-server",
        "sudo /usr/libexec/openssh/sftp-server",
        "sudo /usr/lib/ssh/sftp-server",
        "sudo sftp-server"
    };

    /// <summary>
    /// Creates an <see cref="SftpClient"/> wired with a sudo execution channel proxy.
    /// </summary>
    public static SftpClient CreateSftpClient(ConnectionInfo connectionInfo, string sudoCommand = "sudo /usr/lib/openssh/sftp-server")
    {
        var defaultFactory = Activator.CreateInstance(ServiceFactoryConcreteType, true)!;

        var proxyFactory = (SudoServiceFactoryProxy)DispatchProxyCreateMethod
            .MakeGenericMethod(ServiceFactoryInterfaceType, typeof(SudoServiceFactoryProxy))
            .Invoke(null, null)!;

        proxyFactory.Target = defaultFactory;
        proxyFactory.SudoCommand = sudoCommand;

        return (SftpClient)SftpClientInternalCtor.Invoke(new object[] { connectionInfo, false, proxyFactory });
    }

    public static object WrapSession(object session, string sudoCommand)
    {
        var proxySession = (SudoSessionProxy)DispatchProxyCreateMethod
            .MakeGenericMethod(SessionInterfaceType, typeof(SudoSessionProxy))
            .Invoke(null, null)!;

        proxySession.Target = session;
        proxySession.SudoCommand = sudoCommand;
        return proxySession;
    }

    public static object WrapChannelSession(object channel, string sudoCommand, Type? interfaceType = null)
    {
        var targetInterface = interfaceType ?? ChannelSessionInterfaceType;
        var proxyChannel = (SudoChannelSessionProxy)DispatchProxyCreateMethod
            .MakeGenericMethod(targetInterface, typeof(SudoChannelSessionProxy))
            .Invoke(null, null)!;

        proxyChannel.Target = channel;
        proxyChannel.SudoCommand = sudoCommand;
        return proxyChannel;
    }

    public class SudoServiceFactoryProxy : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public string SudoCommand { get; set; } = "sudo /usr/lib/openssh/sftp-server";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;

            if (targetMethod.Name == "CreateSftpSession" && args != null && args.Length > 0 && args[0] != null)
            {
                // Wrap the ISession argument so that when SftpSession calls CreateChannelSession(),
                // we return our intercepted channel.
                args[0] = WrapSession(args[0]!, SudoCommand);
            }

            return targetMethod.Invoke(Target, args);
        }
    }

    public class SudoSessionProxy : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public string SudoCommand { get; set; } = "sudo /usr/lib/openssh/sftp-server";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;

            var result = targetMethod.Invoke(Target, args);

            if (targetMethod.Name == "CreateChannelSession" && result != null)
            {
                return WrapChannelSession(result, SudoCommand);
            }

            return result;
        }
    }

    public class SudoChannelSessionProxy : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public string SudoCommand { get; set; } = "sudo /usr/lib/openssh/sftp-server";
        public string? ExecutedCommand { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;

            // Intercept SendSubsystemRequest("sftp") and invoke SendExecRequest(SudoCommand) instead.
            if (targetMethod.Name == "SendSubsystemRequest" &&
                args != null &&
                args.Length == 1 &&
                args[0] is string s &&
                string.Equals(s, "sftp", StringComparison.OrdinalIgnoreCase))
            {
                var sendExec = Target.GetType().GetMethod("SendExecRequest", new[] { typeof(string) });
                if (sendExec != null)
                {
                    ExecutedCommand = SudoCommand;
                    return sendExec.Invoke(Target, new object[] { SudoCommand });
                }
            }

            return targetMethod.Invoke(Target, args);
        }
    }
}
