﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Extensions;

#if !SUPPORTS_HOST_ENVIRONMENT
using IHostEnvironment = Microsoft.Extensions.Hosting.IHostingEnvironment;
#endif

namespace OpenIddict.Client.SystemIntegration;

/// <summary>
/// Contains the methods required to ensure that the OpenIddict client system integration configuration is valid.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class OpenIddictClientSystemIntegrationConfiguration : IConfigureOptions<OpenIddictClientOptions>,
                                                                     IPostConfigureOptions<OpenIddictClientOptions>,
                                                                     IPostConfigureOptions<OpenIddictClientSystemIntegrationOptions>
{
    private readonly IHostEnvironment _environment;

    /// <summary>
    /// Creates a new instance of the <see cref="OpenIddictClientSystemIntegrationConfiguration"/> class.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    public OpenIddictClientSystemIntegrationConfiguration(IHostEnvironment environment)
        => _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <inheritdoc/>
    public void Configure(OpenIddictClientOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // Register the built-in event handlers used by the OpenIddict client system integration components.
        options.Handlers.AddRange(OpenIddictClientSystemIntegrationHandlers.DefaultHandlers);
    }

    /// <inheritdoc/>
    public void PostConfigure(string? name, OpenIddictClientOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // If no explicit client URI was set, default to the static "http://localhost/" address, which is
        // adequate for a native/mobile client and points to the embedded web server when it is enabled.
        options.ClientUri ??= new Uri("http://localhost/", UriKind.Absolute);
    }

    /// <inheritdoc/>
    public void PostConfigure(string? name, OpenIddictClientSystemIntegrationOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Create("ios")) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(SR.GetResourceString(SR.ID0389));
        }

#pragma warning disable CA1416
        // If explicitly set, ensure the specified authentication mode is supported.
        if (options.AuthenticationMode is OpenIddictClientSystemIntegrationAuthenticationMode.ASWebAuthenticationSession &&
            !OpenIddictClientSystemIntegrationHelpers.IsASWebAuthenticationSessionSupported())
        {
            throw new PlatformNotSupportedException(SR.GetResourceString(SR.ID0446));
        }

        else if (options.AuthenticationMode is OpenIddictClientSystemIntegrationAuthenticationMode.WebAuthenticationBroker &&
            !OpenIddictClientSystemIntegrationHelpers.IsWebAuthenticationBrokerSupported())
        {
            throw new PlatformNotSupportedException(SR.GetResourceString(SR.ID0392));
        }
#pragma warning restore CA1416

        options.AuthenticationMode ??= OpenIddictClientSystemIntegrationHelpers.IsASWebAuthenticationSessionSupported() ?
            OpenIddictClientSystemIntegrationAuthenticationMode.ASWebAuthenticationSession :
            OpenIddictClientSystemIntegrationAuthenticationMode.SystemBrowser;

        options.EnableActivationHandling    ??= !RuntimeInformation.IsOSPlatform(OSPlatform.Create("ios"));
        options.EnableActivationRedirection ??= !RuntimeInformation.IsOSPlatform(OSPlatform.Create("ios"));
        options.EnablePipeServer            ??= !RuntimeInformation.IsOSPlatform(OSPlatform.Create("ios"));
        options.EnableEmbeddedWebServer     ??= !RuntimeInformation.IsOSPlatform(OSPlatform.Create("ios")) && HttpListener.IsSupported;

        // If no explicit application discriminator was specified, compute the SHA-256 hash
        // of the application name resolved from the host and use it as a unique identifier.
        if (string.IsNullOrEmpty(options.ApplicationDiscriminator))
        {
            if (string.IsNullOrEmpty(_environment.ApplicationName))
            {
                throw new InvalidOperationException(SR.GetResourceString(SR.ID0386));
            }

            options.ApplicationDiscriminator = Base64UrlEncoder.Encode(
                OpenIddictHelpers.ComputeSha256Hash(
                    Encoding.UTF8.GetBytes(_environment.ApplicationName)));
        }

        // If no explicit instance identifier was specified, use a random GUID.
        if (string.IsNullOrEmpty(options.InstanceIdentifier))
        {
            options.InstanceIdentifier = Guid.NewGuid().ToString();
        }

        // If no explicit pipe name was specified, build one using the application discriminator.
        if (string.IsNullOrEmpty(options.PipeName))
        {
            var builder = new StringBuilder();

            // Note: on Windows, the name is deliberately prefixed with "LOCAL\" to support
            // partial trust/sandboxed applications that are executed in an AppContainer
            // and cannot communicate with applications outside the sandbox container.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                builder.Append(@"LOCAL\");
            }

            options.PipeName = builder.Append("OpenIddict.Client.SystemIntegration")
                .Append('-')
                .Append(options.ApplicationDiscriminator)
                .ToString();
        }

#if SUPPORTS_CURRENT_USER_ONLY_PIPE_OPTION
        if (options.PipeOptions is null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Note: the CurrentUserOnly option is also supported on Windows, but is less
            // flexible than using a PipeSecurity object (e.g cross-process communication
            // between elevated and non-elevated processes is not possible with this option).
            // As such, it's not used on Windows (instead, an ACL-based PipeSecurity is used).
            options.PipeOptions = PipeOptions.CurrentUserOnly;
        }
#endif

        // Always configure the pipe to use asynchronous operations,
        // even if the flag was not explicitly set by the user.
        options.PipeOptions |= PipeOptions.Asynchronous;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // If no explicit pipe security policy was specified, grant the current user
            // full control over the created pipe and allow cross-process communication
            // between elevated and non-elevated processes. Note: if the process executes
            // inside an AppContainer, don't override the default OS pipe security policy
            // to allow all applications with the same identity to access the named pipe.
            if (options.PipeSecurity is null)
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);

                if (!IsRunningInAppContainer(identity))
                {
                    options.PipeSecurity = new PipeSecurity();
                    options.PipeSecurity.SetOwner(identity.User!);
                    options.PipeSecurity.AddAccessRule(new PipeAccessRule(identity.User!,
                        PipeAccessRights.FullControl, AccessControlType.Allow));
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            [SupportedOSPlatform("windows")]
            static bool IsRunningInAppContainer(WindowsIdentity identity)
                => OpenIddictClientSystemIntegrationHelpers.IsWindowsVersionAtLeast(10, 0, 10240) &&
                   OpenIddictClientSystemIntegrationHelpers.HasAppContainerToken(identity);
        }
    }
}
