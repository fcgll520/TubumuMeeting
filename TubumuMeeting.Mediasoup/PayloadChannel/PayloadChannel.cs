﻿using System;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tubumu.Core.Extensions;
using Tubumu.Core.Extensions.Object;
using TubumuMeeting.Core;
using TubumuMeeting.Libuv;

namespace TubumuMeeting.Mediasoup
{
    public class PayloadChannel
    {
        #region Constants

        private const int NsMessageMaxLen = 4194313;

        private const int NsPayloadMaxLen = 4194304;

        #endregion

        #region Private Fields

        // Logger
        private readonly ILogger<PayloadChannel> _logger;

        // Unix Socket instance for sending messages to the worker process.
        private readonly UVStream _producerSocket;

        // Unix Socket instance for receiving messages to the worker process.
        private readonly UVStream _consumerSocket;

        // Worker process PID.
        private readonly int _processId;

        // Closed flag.
        private bool _closed = false;

        // Buffer for reading messages from the worker.
        private ArraySegment<byte>? _recvBuffer;

        // Ongoing notification (waiting for its payload).
        private OngoingNotification? _ongoingNotification;

        #endregion

        #region Events

        public event Action<string, string, NotifyData, ArraySegment<byte>>? MessageEvent;

        #endregion

        public PayloadChannel(ILogger<PayloadChannel> logger, UVStream producerSocket, UVStream consumerSocket, int processId)
        {
            _logger = logger;

            _logger.LogDebug("PayloadChannel() | constructor");

            _producerSocket = producerSocket;
            _consumerSocket = consumerSocket;
            _processId = processId;

            _consumerSocket.Data += ConsumerSocketOnData;
            _consumerSocket.Closed += ConsumerSocketOnClosed;
            _consumerSocket.Error += ConsumerSocketOnError;
            _producerSocket.Closed += ProducerSocketOnClosed;
            _producerSocket.Error += ProducerSocketOnError;
        }

        public void Close()
        {
            if (_closed)
                return;

            _logger.LogDebug("Close()");

            _closed = true;

            // Remove event listeners but leave a fake 'error' hander to avoid
            // propagation.
            _consumerSocket.Closed -= ConsumerSocketOnClosed;
            _consumerSocket.Error -= ConsumerSocketOnError;

            _producerSocket.Closed -= ProducerSocketOnClosed;
            _producerSocket.Error -= ProducerSocketOnError;

            // Destroy the socket after a while to allow pending incoming messages.
            // 在 Node.js 实现中，延迟了 200 ms。
            try
            {
                _producerSocket.Close();
            }
            catch (Exception)
            {

            }

            try
            {
                _consumerSocket.Close();
            }
            catch (Exception)
            {

            }
        }

        public void Notify(string @event, object @internal, NotifyData data, byte[] payload)
        {
            _logger.LogDebug($"notify() [event:{@event}]");

            if (_closed)
                throw new InvalidStateException("PayloadChannel closed");

            var notification = new { @event, @internal, data };
            var ns1Bytes = Netstring.Encode(notification.ToCamelCaseJson());
            var ns2Bytes = Netstring.Encode(payload);

            if (ns1Bytes.Length > NsMessageMaxLen)
            {
                throw new Exception("PayloadChannel notification too big");
            }
            if (ns2Bytes.Length > NsMessageMaxLen)
            {
                throw new Exception("PayloadChannel payload too big");
            }

            Loop.Default.Sync(() =>
            {
                try
                {
                    // This may throw if closed or remote side ended.
                    _producerSocket.Write(ns1Bytes, ex =>
                    {
                        if (ex != null)
                        {
                            _logger.LogError(ex, "_producerSocket.Write() | error");
                        }
                    });

                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"notify() | sending notification failed: {ex}");
                    return;
                }

                try
                {
                    // This may throw if closed or remote side ended.
                    _producerSocket.Write(ns2Bytes, ex =>
                    {
                        if (ex != null)
                        {
                            _logger.LogError(ex, "_producerSocket.Write() | error");
                        }
                    });

                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"notify() | sending notification failed: {ex}");
                    return;
                }
            });
        }

        #region Event handles

        private void ConsumerSocketOnData(ArraySegment<byte> data)
        {
            if (_recvBuffer == null)
            {
                _recvBuffer = data;
            }
            else
            {
                var newBuffer = new byte[_recvBuffer.Value.Count + data.Count];
                Array.Copy(_recvBuffer.Value.Array, _recvBuffer.Value.Offset, newBuffer, 0, _recvBuffer.Value.Count);
                Array.Copy(data.Array, data.Offset, newBuffer, _recvBuffer.Value.Count, data.Count);
                _recvBuffer = new ArraySegment<byte>(newBuffer);
            }

            if (_recvBuffer.Value.Count > NsPayloadMaxLen)
            {
                _logger.LogError("ConsumerSocketOnData() | receiving buffer is full, discarding all data into it");
                // Reset the buffer and exit.
                _recvBuffer = null;
                return;
            }

            //_logger.LogError($"ConsumerSocketOnData: {buffer}");
            var netstring = new Netstring(_recvBuffer.Value);
            try
            {
                var nsLength = 0;
                foreach (var payload in netstring)
                {
                    nsLength += payload.NetstringLength;
                    ProcessMessage(payload);
                }

                if (nsLength > 0)
                {
                    if (nsLength == _recvBuffer.Value.Count)
                    {
                        // Reset the buffer.
                        _recvBuffer = null;
                    }
                    else
                    {
                        _recvBuffer = new ArraySegment<byte>(_recvBuffer.Value.Array, _recvBuffer.Value.Offset + nsLength, _recvBuffer.Value.Count - nsLength);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"ConsumerSocketOnData() | invalid netstring data received from the worker process:{ex}");
                // Reset the buffer and exit.
                _recvBuffer = null;
                return;
            }
        }

        private void ConsumerSocketOnClosed()
        {
            _logger.LogDebug("ConsumerSocketOnClosed() | Consumer Channel ended by the worker process");
        }

        private void ConsumerSocketOnError(Exception exception)
        {
            _logger.LogDebug("ConsumerSocketOnError() | Consumer Channel error", exception);
        }

        private void ProducerSocketOnClosed()
        {
            _logger.LogDebug("ProducerSocketOnClosed() | Producer Channel ended by the worker process");
        }

        private void ProducerSocketOnError(Exception exception)
        {
            _logger.LogDebug("ProducerSocketOnError() | Producer Channel error", exception);
        }

        #endregion

        #region Private Methods

        private void ProcessMessage(Payload payload)
        {
            if (_ongoingNotification == null)
            {
                var payloadString = Encoding.UTF8.GetString(payload.Data.Array, payload.Data.Offset, payload.Data.Count);
                var msg = JObject.Parse(payloadString);
                var targetId = msg["targetId"].Value(String.Empty);
                var @event = msg["event"].Value(string.Empty);
                var data = msg["data"].Value(string.Empty);

                if (!targetId.IsNullOrWhiteSpace() && !@event.IsNullOrWhiteSpace())
                {
                    _logger.LogError("received message is not a notification");
                    return;
                }

                var notifyData = JsonConvert.DeserializeObject<NotifyData>(data);

                _ongoingNotification = new OngoingNotification
                {
                    TargetId = targetId,
                    Event = @event,
                    Data = notifyData,
                };
            }
            else
            {
                // Emit the corresponding event.
                MessageEvent?.Invoke(_ongoingNotification.TargetId, _ongoingNotification.Event, _ongoingNotification.Data, payload.Data);

                // Unset ongoing notification.
                _ongoingNotification = null;
            }
        }

        #endregion
    }
}
