// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.SignalR.Client.SourceGenerator
{
    internal partial class CallbackRegistrationGenerator
    {
        public class Emitter
        {
            private readonly GeneratorExecutionContext _context;
            private readonly SourceGenerationSpec _spec;

            public Emitter(GeneratorExecutionContext context, SourceGenerationSpec spec)
            {
                _context = context;
                _spec = spec;
            }

            public void Emit()
            {
                // Generate extensions and other user facing mostly-static code in a single source file
                EmitExtensions();
                // Generate specific callback registration methods in their own source file for each provider type
                foreach (var typeSpec in _spec.Types)
                {
                    EmitRegistrationMethod(typeSpec);
                }
            }

            private void EmitExtensions()
            {
                var registerProviderBody = new StringBuilder();

                // Generate body of RegisterCallbackProvider<T>
                foreach (var typeSpec in _spec.Types)
                {
                    var methodName = $"register{typeSpec.TypeName}";
                    var fqtn = typeSpec.FullyQualifiedTypeName;
                    registerProviderBody.AppendLine($@"
            if(typeof(T) == typeof({fqtn}))
            {{
                return (System.IDisposable) new CallbackProviderRegistration({methodName}(connection, ({fqtn}) provider));
            }}");
                }

                // Generate RegisterCallbackProvider<T> extension method and CallbackProviderRegistration class
                // RegisterCallbackProvider<T> is used by end-user to register their callback provider types
                // CallbackProviderRegistration is a private implementation of IDisposable which simply holds
                //  an array of IDisposables acquired from registration of each callback method from HubConnection
                var extensions = $@"// Generated by Microsoft.AspNetCore.Client.SourceGenerator
using Microsoft.AspNetCore.SignalR.Client;
namespace Microsoft.AspNetCore.SignalR.Client
{{
    public static partial class HubConnectionExtensionsGenerated
    {{
        public static System.IDisposable RegisterCallbackProvider<T>(this HubConnection connection, T provider)
        {{
{registerProviderBody.ToString()}
            throw new System.ArgumentException(nameof(T));
        }}

        private sealed class CallbackProviderRegistration : System.IDisposable
        {{
            private System.IDisposable[] registrations;
            public CallbackProviderRegistration(params System.IDisposable[] registrations)
            {{
                this.registrations = registrations;
            }}

            public void Dispose()
            {{
                if(this.registrations is null)
                {{
                    return;
                }}

                System.Collections.Generic.List<System.Exception> exceptions = null;
                foreach(var registration in this.registrations)
                {{
                    try
                    {{
                        registration.Dispose();
                    }}
                    catch (System.Exception exc)
                    {{
                        if(exceptions is null)
                        {{
                            exceptions = new ();
                        }}

                        exceptions.Add(exc);
                    }}
                }}
                this.registrations = null;
                if(exceptions is not null)
                {{
                    throw new System.AggregateException(exceptions);
                }}
            }}
        }}
    }}
}}";

                _context.AddSource("CallbackRegistration.g.cs", SourceText.From(extensions.ToString(), Encoding.UTF8));
            }

            private void EmitRegistrationMethod(TypeSpec typeSpec)
            {
                // The actual registration method goes thru each method that the callback provider type has and then
                //  registers the method with HubConnection and stashes the returned IDisposable into an array for
                //  later consumption by CallbackProviderRegistration's constructor
                var registrationMethodBody = new StringBuilder($@"
using Microsoft.AspNetCore.SignalR.Client;
namespace Microsoft.AspNetCore.SignalR.Client
{{
    public static partial class HubConnectionExtensionsGenerated
    {{
        private static System.IDisposable[] register{typeSpec.TypeName}(HubConnection connection, {typeSpec.FullyQualifiedTypeName} provider)
        {{
            var registrations = new System.IDisposable[{typeSpec.Methods.Count}];");

                // Generate each of the methods
                var i = 0;
                foreach (var member in typeSpec.Methods)
                {
                    var genericArgs = new StringBuilder();
                    var lambaParams = new StringBuilder();

                    // Populate call with its parameters
                    var first = true;
                    foreach (var parameter in member.Arguments)
                    {
                        if (first)
                        {
                            genericArgs.Append('<');
                            lambaParams.Append('(');
                        }
                        else
                        {
                            genericArgs.Append(", ");
                            lambaParams.Append(", ");
                        }

                        first = false;
                        genericArgs.Append($"{parameter.FullyQualifiedTypeName}");
                        lambaParams.Append($"{parameter.Name}");
                    }

                    if (!first)
                    {
                        genericArgs.Append('>');
                        lambaParams.Append(')');
                    }
                    else
                    {
                        lambaParams.Append("()");
                    }


                    var lambda = $"{lambaParams} => provider.{member.Name}{lambaParams}";
                    var call = $"connection.On{genericArgs}(\"{member.Name}\", {lambda})";

                    registrationMethodBody.AppendLine($@"
            registrations[{i}] = {call};");
                    ++i;
                }
                registrationMethodBody.AppendLine(@"
            return registrations;
        }
    }
}");

                _context.AddSource($"CallbackRegistration.{typeSpec.TypeName}.g.cs", SourceText.From(registrationMethodBody.ToString(), Encoding.UTF8));
            }
        }
    }
}
