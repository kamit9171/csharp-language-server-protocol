using System.Collections.Generic;
using MediatR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.DebugAdapter.Protocol.Serialization;
using OmniSharp.Extensions.JsonRpc;

namespace OmniSharp.Extensions.DebugAdapter.Protocol.Requests
{
    [Method(RequestNames.Launch, Direction.ClientToServer)]
    public class LaunchRequestArguments : IRequest<LaunchResponse>
    {
        /// <summary>
        /// If noDebug is true the launch request should launch the program without enabling debugging.
        /// </summary>
        [Optional]
        public bool? NoDebug { get; set; }

        /// <summary>
        /// Optional data from the previous, restarted session.
        /// The data is sent as the 'restart' attribute of the 'terminated' event.
        /// The client should leave the data intact.
        /// </summary>
        [Optional]
        [JsonProperty(PropertyName = "__restart")]
        public JToken Restart { get; set; }

        [JsonExtensionData] public IDictionary<string, object> ExtensionData { get; set; } = new Dictionary<string, object>();
    }
}
