﻿using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace OmniSharp.Extensions.DebugAdapter.Protocol.Serialization
{
    internal class ContractResolver : DefaultContractResolver
    {
        public ContractResolver() => NamingStrategy = new CamelCaseNamingStrategy(true, false, true);

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            if (member.GetCustomAttributes<OptionalAttribute>(true).Any()
             || property.DeclaringType.Name.EndsWith("Capabilities")
            )
            {
                property.NullValueHandling = NullValueHandling.Ignore;
            }

            return property;
        }
    }
}
