﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using OmniSharp.Extensions.JsonRpc;

namespace OmniSharp.Extensions.LanguageServer.Protocol.Shared
{
    public interface ILspHandlerTypeDescriptorProvider : IHandlerTypeDescriptorProvider<ILspHandlerTypeDescriptor>
    {
        string GetMethodForRegistrationOptions(object registrationOptions);
        Type GetRegistrationType(string method);
    }

    public class LspHandlerTypeDescriptorProvider : ILspHandlerTypeDescriptorProvider, IHandlerTypeDescriptorProvider<IHandlerTypeDescriptor>
    {
        private readonly ConcurrentDictionary<Type, string> MethodNames = new ConcurrentDictionary<Type, string>();

        internal readonly ILookup<string, ILspHandlerTypeDescriptor> KnownHandlers;

        internal LspHandlerTypeDescriptorProvider(IEnumerable<Assembly> assemblies)
        {
            KnownHandlers = HandlerTypeDescriptorProvider
                           .GetDescriptors(assemblies)
                           .Select(x => new LspHandlerTypeDescriptor(x.HandlerType) as ILspHandlerTypeDescriptor)
                           .ToLookup(x => x.Method, x => x, StringComparer.Ordinal);
        }

        public string GetMethodForRegistrationOptions(object registrationOptions)
        {
            var registrationType = registrationOptions.GetType();
            var interfaces = new HashSet<Type>(
                registrationOptions.GetType().GetInterfaces()
                                   .Except(registrationType.BaseType?.GetInterfaces() ?? Enumerable.Empty<Type>())
            );
            return interfaces.SelectMany(
                                  x =>
                                      KnownHandlers.SelectMany(z => z)
                                                   .Where(z => z.HasRegistration)
                                                   .Where(z => x.IsAssignableFrom(z.RegistrationType))
                              )
                             .FirstOrDefault()?.Method;
        }

        public Type GetRegistrationType(string method) => KnownHandlers[method]
                                                         .Where(z => z.HasRegistration)
                                                         .Select(z => z.RegistrationType)
                                                         .FirstOrDefault();

        public ILspHandlerTypeDescriptor GetHandlerTypeDescriptor<A>() => GetHandlerTypeDescriptor(typeof(A));
        IHandlerTypeDescriptor IHandlerTypeDescriptorProvider<IHandlerTypeDescriptor>.GetHandlerTypeDescriptor(Type type) => GetHandlerTypeDescriptor(type);

        IHandlerTypeDescriptor IHandlerTypeDescriptorProvider<IHandlerTypeDescriptor>.GetHandlerTypeDescriptor<A>() => GetHandlerTypeDescriptor<A>();

        public ILspHandlerTypeDescriptor GetHandlerTypeDescriptor(Type type)
        {
            var @default = KnownHandlers
                          .SelectMany(g => g)
                          .FirstOrDefault(x => x.InterfaceType == type || x.HandlerType == type || x.ParamsType == type)
                        ?? KnownHandlers
                          .SelectMany(g => g)
                          .FirstOrDefault(x => x.InterfaceType.IsAssignableFrom(type) || x.HandlerType.IsAssignableFrom(type));
            if (@default != null)
            {
                return @default;
            }

            var methodName = GetMethodName(type);
            return string.IsNullOrWhiteSpace(methodName) ? null : KnownHandlers[methodName].FirstOrDefault();
        }

        public string GetMethodName<T>() where T : IJsonRpcHandler => GetMethodName(typeof(T));

        public bool IsMethodName(string name, params Type[] types) => types.Any(z => GetMethodName(z).Equals(name));

        public string GetMethodName(Type type)
        {
            if (MethodNames.TryGetValue(type, out var method)) return method;

            // Custom method
            var attribute = MethodAttribute.From(type);

            var handler = KnownHandlers.SelectMany(z => z)
                                       .FirstOrDefault(z => z.InterfaceType == type || z.HandlerType == type || z.ParamsType == type);
            if (handler != null)
            {
                return handler.Method;
            }

            // TODO: Log unknown method name
            if (attribute is null)
            {
                return null;
            }

            MethodNames.TryAdd(type, attribute.Method);
            return attribute.Method;
        }
    }
}
