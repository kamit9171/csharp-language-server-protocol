using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using Newtonsoft.Json;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.JsonRpc.Server.Messages;

namespace OmniSharp.Extensions.JsonRpc
{
    public class InputHandler : IInputHandler, IDisposable
    {
        public static readonly byte[] HeadersFinished =
            new byte[] {(byte) '\r', (byte) '\n', (byte) '\r', (byte) '\n'}.ToArray();

        public const int HeadersFinishedLength = 4;
        public static readonly char[] HeaderKeys = {'\r', '\n', ':'};
        public const short MinBuffer = 21; // Minimum size of the buffer "Content-Length: X\r\n\r\n"
        public static readonly byte[] ContentLength = "Content-Length".Select(x => (byte) x).ToArray();
        public static readonly int ContentLengthLength = 14;

        private readonly PipeReader _pipeReader;
        private readonly IOutputHandler _outputHandler;
        private readonly IReceiver _receiver;
        private readonly IRequestProcessIdentifier _requestProcessIdentifier;
        private readonly IRequestRouter<IHandlerDescriptor> _requestRouter;
        private readonly IResponseRouter _responseRouter;
        private readonly Action<Exception> _unhandledInputProcessException;
        private readonly Func<ServerError, IHandlerDescriptor, Exception> _getException;
        private readonly ILogger<InputHandler> _logger;
        private readonly ProcessScheduler _scheduler;
        private readonly Memory<byte> _headersBuffer;
        private readonly Memory<byte> _contentLengthBuffer;
        private readonly byte[] _contentLengthValueBuffer;
        private readonly Memory<byte> _contentLengthValueMemory;
        private readonly CancellationTokenSource _stopProcessing;
        private readonly CompositeDisposable _disposable;
        private readonly AsyncSubject<Unit> _inputActive;

        public InputHandler(
            PipeReader pipeReader,
            IOutputHandler outputHandler,
            IReceiver receiver,
            IRequestProcessIdentifier requestProcessIdentifier,
            IRequestRouter<IHandlerDescriptor> requestRouter,
            IResponseRouter responseRouter,
            ILoggerFactory loggerFactory,
            Action<Exception> unhandledInputProcessException,
            Func<ServerError, IHandlerDescriptor, Exception> getException,
            bool supportContentModified,
            int? concurrency
        )
        {
            _pipeReader = pipeReader;
            _outputHandler = outputHandler;
            _receiver = receiver;
            _requestProcessIdentifier = requestProcessIdentifier;
            _requestRouter = requestRouter;
            _responseRouter = responseRouter;
            _unhandledInputProcessException = unhandledInputProcessException;
            _getException = getException;
            _logger = loggerFactory.CreateLogger<InputHandler>();
            _scheduler = new ProcessScheduler(loggerFactory, supportContentModified, concurrency, TaskPoolScheduler.Default);
            _headersBuffer = new Memory<byte>(new byte[HeadersFinishedLength]);
            _contentLengthBuffer = new Memory<byte>(new byte[ContentLengthLength]);
            _contentLengthValueBuffer = new byte[20]; // Max string length of the long value
            _contentLengthValueMemory =
                new Memory<byte>(_contentLengthValueBuffer); // Max string length of the long value
            _stopProcessing = new CancellationTokenSource();

            _disposable = new CompositeDisposable {
                Disposable.Create(() => _stopProcessing.Cancel()),
                _stopProcessing,
                _scheduler,
            };

            _inputActive = new AsyncSubject<Unit>();
        }

        public void Start()
        {
            Observable.FromAsync(() => ProcessInputStream(_stopProcessing.Token))
                .Do(_ => { }, e => _logger.LogCritical(e, "unhandled exception"))
                .Subscribe(_inputActive);
        }

        public async Task StopAsync()
        {
            await _outputHandler.StopAsync();
            await _pipeReader.CompleteAsync();
        }

        public void Dispose()
        {
            _disposable.Dispose();
            _pipeReader.Complete();
            _outputHandler.Dispose();
        }

        public Task InputCompleted => _inputActive.ToTask();

        private bool TryParseHeaders(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
        {
            if (buffer.Length < MinBuffer || buffer.Length < HeadersFinishedLength)
            {
                line = default;
                return false;
            }

            var rentedSpan = _headersBuffer.Span;

            var start = buffer.PositionOf((byte) '\r');
            do
            {
                if (!start.HasValue)
                {
                    line = default;
                    return false;
                }

                var next = buffer.Slice(start.Value, buffer.GetPosition(HeadersFinishedLength, start.Value));
                next.CopyTo(rentedSpan);
                if (IsEqual(rentedSpan, HeadersFinished))
                {
                    line = buffer.Slice(0, next.End);
                    buffer = buffer.Slice(next.End);
                    return true;
                }

                start = buffer.Slice(buffer.GetPosition(HeadersFinishedLength, start.Value)).PositionOf((byte) '\r');
            } while (start.HasValue && buffer.Length > MinBuffer);

            line = default;
            return false;
        }

        static bool IsEqual(in Span<byte> headers, in byte[] bytes)
        {
            var isEqual = true;
            var len = bytes.Length;
            for (var i = 0; i < len; i++)
            {
                if (bytes[i] == headers[i]) continue;
                isEqual = false;
                break;
            }

            return isEqual;
        }

        private bool TryParseBodyString(in long length, ref ReadOnlySequence<byte> buffer,
            out ReadOnlySequence<byte> line)
        {
            if (buffer.Length < length)
            {
                line = default;
                return false;
            }


            line = buffer.Slice(0, length);
            buffer = buffer.Slice(length);
            return true;
        }

        bool TryParseContentLength(ref ReadOnlySequence<byte> buffer, out long length)
        {
            do
            {
                var colon = buffer.PositionOf((byte) ':');
                if (!colon.HasValue)
                {
                    length = -1;
                    return false;
                }

                var slice = buffer.Slice(0, colon.Value);
                slice.CopyTo(_contentLengthBuffer.Span);

                if (IsEqual(_contentLengthBuffer.Span, ContentLength))
                {
                    var position = buffer.GetPosition(1, colon.Value);
                    var offset = 1;

                    while (buffer.TryGet(ref position, out var memory, true) && !memory.Span.IsEmpty)
                    {
                        foreach (var t in memory.Span)
                        {
                            if (t == (byte) ' ')
                            {
                                offset++;
                                continue;
                            }

                            break;
                        }
                    }

                    var lengthSlice = buffer.Slice(
                        buffer.GetPosition(offset, colon.Value),
                        buffer.PositionOf((byte) '\r') ?? buffer.End
                    );

                    var whitespacePosition = lengthSlice.PositionOf((byte) ' ');
                    if (whitespacePosition.HasValue)
                    {
                        lengthSlice = lengthSlice.Slice(0, whitespacePosition.Value);
                    }

                    lengthSlice.CopyTo(_contentLengthValueMemory.Span);
                    if (long.TryParse(Encoding.ASCII.GetString(_contentLengthValueBuffer), out length))
                    {
                        // Reset the array otherwise smaller numbers will be inflated;
                        for (var i = 0; i < lengthSlice.Length; i++) _contentLengthValueMemory.Span[i] = 0;
                        return true;
                    }
                    // Reset the array otherwise smaller numbers will be inflated;
                    for (var i = 0; i < lengthSlice.Length; i++) _contentLengthValueMemory.Span[i] = 0;

                    _logger.LogError("Unable to get length from content length header...");
                    return false;
                }
                else
                {
                    buffer = buffer.Slice(buffer.GetPosition(1, buffer.PositionOf((byte) '\n') ?? buffer.End));
                }
            } while (true);
        }

        internal async Task ProcessInputStream(CancellationToken cancellationToken)
        {
            // some time to attach a debugger
            // System.Threading.Thread.Sleep(TimeSpan.FromSeconds(5));
            ReadOnlySequence<byte> buffer = default;
            try
            {

                var headersParsed = false;
                long length = 0;
                do
                {
                    var result = await _pipeReader.ReadAsync(cancellationToken);
                    buffer = result.Buffer;

                    var dataParsed = true;
                    do
                    {
                        dataParsed = false;
                        if (!headersParsed)
                        {
                            if (TryParseHeaders(ref buffer, out var line))
                            {
                                if (TryParseContentLength(ref line, out length))
                                {
                                    headersParsed = true;
                                }
                            }
                        }

                        if (headersParsed && length == 0)
                        {
                            HandleRequest(new ReadOnlySequence<byte>(Array.Empty<byte>()));
                            headersParsed = false;
                        }

                        if (headersParsed)
                        {
                            if (TryParseBodyString(length, ref buffer, out var line))
                            {
                                headersParsed = false;
                                length = 0;
                                HandleRequest(line);
                                dataParsed = true;
                            }
                        }
                    } while (!buffer.IsEmpty && dataParsed);

                    _pipeReader.AdvanceTo(buffer.Start, buffer.End);

                    // Stop reading if there's no more data coming.
                    if (result.IsCompleted && buffer.IsEmpty)
                    {
                        break;
                    }
                } while (!cancellationToken.IsCancellationRequested);
            }
            catch (Exception e)
            {
                var outerException = new InputProcessingException(Encoding.UTF8.GetString(buffer.ToArray()), e);
                await _outputHandler.StopAsync();
                await _pipeReader.CompleteAsync();

                _unhandledInputProcessException(outerException);
                throw outerException;
            }
        }

        private void HandleRequest(in ReadOnlySequence<byte> request)
        {
            JToken payload;
            try
            {
                using var textReader = new StreamReader(request.AsStream());
                using var reader = new JsonTextReader(textReader);
                payload = JToken.Load(reader);
            }
            catch
            {
                _outputHandler.Send(new ParseError());
                return;
            }

            if (!_receiver.IsValid(payload))
            {
                _outputHandler.Send(new InvalidRequest());
                return;
            }

            var (requests, hasResponse) = _receiver.GetRequests(payload);
            if (hasResponse)
            {
                foreach (var response in requests.Where(x => x.IsResponse).Select(x => x.Response))
                {
                    var id = response.Id is string s ? long.Parse(s) : response.Id is long l ? l : -1;
                    if (id < 0) continue;

                    var tcs = _responseRouter.GetRequest(id);
                    if (tcs is null) continue;

                    if (response is ServerResponse serverResponse)
                    {
                        tcs.SetResult(serverResponse.Result);
                    }
                    else if (response is ServerError serverError)
                    {
                        tcs.SetException(DefaultErrorParser(_requestRouter.GetRequestDescriptor(response.Id), serverError, _getException));
                    }
                }

                return;
            }

            foreach (var item in requests)
            {
                if (item.IsRequest)
                {
                    var descriptor = _requestRouter.GetDescriptor(item.Request);
                    if (descriptor is null)
                    {
                        _outputHandler.Send(new MethodNotFound(item.Request.Id, item.Request.Method));
                        return;
                    }

                    var type = _requestProcessIdentifier.Identify(descriptor);
                    _requestRouter.StartRequest(item.Request.Id, descriptor);
                    _scheduler.Add(
                        type,
                        item.Request.Method,
                        contentModifiedToken => Observable.FromAsync(async (ct) => {
                                var result =
                                    await _requestRouter.RouteRequest(descriptor, item.Request, ct, ObservableToToken(contentModifiedToken));
                                _outputHandler.Send(result.Value);
                            }
                        ));
                }

                if (item.IsNotification)
                {
                    var descriptor = _requestRouter.GetDescriptor(item.Notification);
                    if (descriptor is null)
                    {
                        // TODO: Figure out a good way to send this feedback back.
                        // _outputHandler.Send(new RpcError(null, new ErrorMessage(-32601, $"Method not found - {item.Notification.Method}")));
                        return;
                    }

                    // We need to special case cancellation so that we can cancel any request that is currently in flight.
                    if (descriptor.Method == JsonRpcNames.CancelRequest)
                    {
                        var cancelParams = item.Notification.Params?.ToObject<CancelParams>();
                        if (cancelParams == null)
                        {
                            continue;
                        }

                        _requestRouter.CancelRequest(cancelParams.Id);
                        continue;
                    }

                    var type = _requestProcessIdentifier.Identify(descriptor);
                    _scheduler.Add(
                        type,
                        item.Notification.Method,
                        contentModifiedToken =>
                            Observable.FromAsync((ct) => _requestRouter.RouteNotification(descriptor, item.Notification, ct, ObservableToToken(contentModifiedToken)))
                    );
                }

                if (item.IsError)
                {
                    // TODO:
                    _outputHandler.Send(item.Error);
                }
            }
        }

        private static CancellationToken ObservableToToken(IObservable<Unit> observable)
        {
            var cts = new CancellationTokenSource();
            observable.Subscribe(_ => { }, () => cts.Cancel());
            return cts.Token;
        }

        private static Exception DefaultErrorParser(IHandlerDescriptor descriptor, ServerError error, Func<ServerError, IHandlerDescriptor, Exception> customHandler)
        {
            return error.Error?.Code switch {
                ErrorCodes.ServerNotInitialized => new ServerNotInitializedException(error.Id),
                ErrorCodes.MethodNotSupported => new MethodNotSupportedException(error.Id, descriptor?.Method ?? "UNKNOWN"),
                ErrorCodes.InvalidRequest => new InvalidRequestException(error.Id),
                ErrorCodes.InvalidParameters => new InvalidParametersException(error.Id),
                ErrorCodes.InternalError => new InternalErrorException(error.Id, error.Error.Data?.ToString() ?? string.Empty),
                ErrorCodes.ParseError => new ParseErrorException(error.Id),
                ErrorCodes.RequestCancelled => new RequestCancelledException(error.Id),
                ErrorCodes.ContentModified => new ContentModifiedException(error.Id),
                ErrorCodes.UnknownErrorCode => new UnknownErrorException(error.Id),
                ErrorCodes.Exception => new JsonRpcException(ErrorCodes.Exception, error.Id, error.Error.Message ?? string.Empty, error.Error.Data.ToString()),
                _ => customHandler?.Invoke(error, descriptor) ??
                     new JsonRpcException(
                        error.Error?.Code ?? ErrorCodes.UnknownErrorCode, error.Id, error.Error?.Message ?? string.Empty,
                        error.Error?.Data.ToString() ?? string.Empty
                     )
            };
        }
    }

    public class InputProcessingException : Exception
    {
        public InputProcessingException(string message, Exception innerException) : base($"There was an error processing input the contents of the buffer were '{message}'", innerException)
        {

        }
    }
}
