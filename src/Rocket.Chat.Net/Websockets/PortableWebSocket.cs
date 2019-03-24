using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using Fleck;
using Rocket.Chat.Net.Portability.Contracts;
using Rocket.Chat.Net.Portability.Websockets;

namespace Rocket.Chat.Net.Websockets
{
    public class PortableWebSocket : PortableWebSocketBase
    {
        private readonly WebSocketServer _socket;
        private IWebSocketConnection _connection;

        public PortableWebSocket(string url) : base(url)
        {
            _socket = new WebSocketServer(url);
        }

        private EventHandler<PortableMessageReceivedEventArgs> _messageReceivedEventHandler;
        public override event EventHandler<PortableMessageReceivedEventArgs> MessageReceived
        {
            add { _messageReceivedEventHandler += (sender, args) => value.Invoke(sender, new PortableMessageReceivedEventArgs(args.Message)); }
            remove { throw new NotImplementedException(); }
        }

        private EventHandler _closedEventHandler;
        public override event EventHandler Closed
        {
            add { _closedEventHandler += value; }
            remove { _closedEventHandler -= value; }
        }

        private EventHandler<PortableErrorEventArgs> _errorEventHandler;
        public override event EventHandler<PortableErrorEventArgs> Error
        {
            add { _errorEventHandler += (sender, args) => value.Invoke(sender, new PortableErrorEventArgs(args.Exception)); }
            remove { throw new NotImplementedException(); }
        }

        private EventHandler _openedEventHandler;
        public override event EventHandler Opened
        {
            add { _openedEventHandler += value; }
            remove { _openedEventHandler -= value; }
        }

        public override void Open()
        {
            _socket.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    _openedEventHandler?.Invoke(socket,new EventArgs());
                };
                socket.OnClose = () =>
                {
                    _closedEventHandler?.Invoke(socket, new EventArgs());
                };
                socket.OnMessage = m =>
                {
                    _messageReceivedEventHandler?.Invoke(socket, new PortableMessageReceivedEventArgs(m));
                };
                socket.OnError = e =>
                {
                    _errorEventHandler?.Invoke(socket,new PortableErrorEventArgs(e));
                };

                _connection = socket;
            });
        }

        public override void Close()
        {
            _socket.Dispose();
        }

        public override void Send(string json)
        {
            _connection.Send(json);
        }
    }
}
