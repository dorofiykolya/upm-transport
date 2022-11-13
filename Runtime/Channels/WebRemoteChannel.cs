﻿#define SUPPRESS_LOST_PACKET_TILL_DELIVERED
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Common;
using UnityEngine.Networking;
using HttpTransport.Transports;

namespace HttpTransport.Channels
{
    public class WebRemoteChannel : IChannel
    {
        public const int TimeoutSeconds = 7;
        public const int RetryCount = 3;

        private readonly Lifetime _lifetime;
        private readonly PacketMessageQueue _packetQueue = new PacketMessageQueue();

        #region Signals

        public ISignal<Packet> OnPacketSent => _onPacketSent;
        private readonly Signal<Packet> _onPacketSent;
        public ISignal<Packet> OnResponse => _onResponse;
        private readonly Signal<Packet> _onResponse;
        public ISignal<Packet> OnSendFailure => _onSendFailure;
        private readonly Signal<Packet> _onSendFailure;

        #endregion

        private Packet _currentPacket;
        private bool _isSending;
        private bool _abortCurrent;

        public WebRemoteChannel(Lifetime lifetime, string uri)
        {
            _onResponse = new Signal<Packet>(lifetime);
            _onSendFailure = new Signal<Packet>(lifetime);
            _onPacketSent = new Signal<Packet>(lifetime);

            Uri = uri;
            _lifetime = lifetime;

            lifetime.AddAction(Dispose);
        }

        private void Dispose()
        {
            foreach (var packet in _packetQueue)
            {
                packet.Response.SetException(PipelineBreak.Break);
            }
        }

        public string Uri { get; set; }

        public bool HasPendingFailure => _currentPacket.Failed;

        public Task<Response> Send(Request request)
        {
            var task = _packetQueue.Enqueue(request);
            SendNext();
            return task;
        }

        public void Retry()
        {
            _currentPacket.Failed = false;
            _currentPacket.FailCount = 0;
            if (!_isSending)
            {
                TrySend();
            }
        }

        public void AbortResend()
        {
            if (_isSending)
            {
                _abortCurrent = true;
            }
            else
            {
                ClearCurrent();
                SendNext();
            }
        }

        private void SendNext()
        {
            if (_isSending || (_packetQueue.Count == 0)) return;

            if (_currentPacket.Failed)
                return;

            _currentPacket = _packetQueue.Dequeue();
            TrySend();
        }

        private void ClearCurrent()
        {
            _abortCurrent = false;
            _currentPacket = default;
            _isSending = false;
        }

        private void TrySend()
        {
            if (_lifetime.IsTerminated) return;
            _isSending = true;
            var webRequest = BuildRequest(_currentPacket);

            var request = webRequest.SendWebRequest();
            _onPacketSent.Fire(_currentPacket);

            request.completed += operation =>
            {
                if (_lifetime.IsTerminated) return;
                var isNetworkError = webRequest.result == UnityWebRequest.Result.ConnectionError;
                if (isNetworkError)
                {
                    _currentPacket.FailCount++;
                    if (_currentPacket.FailCount < RetryCount && !_abortCurrent)
                    {
                        _onSendFailure.Fire(_currentPacket);
                        if (!_abortCurrent)
                        {
                            webRequest.Dispose();
                            TrySend();
                            return;
                        }
                    }

                    _currentPacket.Failed = true;
                    _onSendFailure.Fire(_currentPacket);
#if SUPPRESS_LOST_PACKET_TILL_DELIVERED
                    if (_abortCurrent || _currentPacket.FailCount >= RetryCount)
                    {
                        /*
                        _currentPacket.Response.SetResult(new Response(_currentPacket.Request,
                                                                       request.webRequest.GetResponseHeaders(),
                                                                       request.webRequest.downloadHandler.data,
                                                                       (long)Shared.Protocol.Api.ResponseStatusCode.GatewayTimeout,
                                                                       true,
                                                                       webRequest.error));
                        */
                        //ClearCurrent();
                        _isSending = false;
                        webRequest.Dispose();
                        //SendNext();
                    }
                    else
                    {
                        _isSending = false;
                        webRequest.Dispose();
                    }

                    return;
#endif
                }

                //todo:resolve multiple callbacks!
                string detail = isNetworkError
                        ? webRequest.error
                        : request.webRequest.downloadHandler.text;
                var headers = request.webRequest.GetResponseHeaders();
                var data = request.webRequest.downloadHandler.data;
                var responseCode = request.webRequest.responseCode;

                _currentPacket.Response.SetResult(new Response(_currentPacket.Request,
                        headers, data, responseCode,
                        isNetworkError, detail));
                _onResponse.Fire(_currentPacket);

                webRequest.Dispose();
                _isSending = false;
                if (!isNetworkError)
                    SendNext();
            };
        }

        private UnityWebRequest BuildRequest(Packet packet)
        {
            if (packet.Request.Method == HttpMethod.Post)
            {
                return BuildPostRequest(packet);
            }
            if (packet.Request.Method == HttpMethod.Get)
            {
                return BuildGetRequest(packet);
            }

            return null;
        }

        private UnityWebRequest BuildGetRequest(Packet packet)
        {
            var webRequest = UnityWebRequest.Get($"{Uri}{packet.Request.Uri}");
            webRequest.timeout = TimeoutSeconds;
            foreach (var pair in packet.Request.Headers)
            {
                webRequest.SetRequestHeader(pair.Key, pair.Value);
            }

            return webRequest;
        }

        private UnityWebRequest BuildPostRequest(Packet packet)
        {
            var webRequest = UnityWebRequest.Post($"{Uri}{packet.Request.Uri}", new List<IMultipartFormSection>());
            webRequest.timeout = TimeoutSeconds;
            foreach (var pair in packet.Request.Headers)
            {
                webRequest.SetRequestHeader(pair.Key, pair.Value);
            }

            webRequest.uploadHandler = new UploadHandlerRaw((byte[])packet.Request.Content);
            return webRequest;
        }

        public struct Packet
        {
            public TaskCompletionSource<Response> Response;
            public Request Request;
            public int FailCount;
            public bool Failed;
        }
    }
}
