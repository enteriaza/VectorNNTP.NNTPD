using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using VectorNNTP.TftpSmoke.Protocol;
using VectorNNTP.TftpSmoke.Transport;

namespace VectorNNTP.TftpSmoke.Tests;

/// <summary>In-process datagram fabric. Drops, duplicates, and reorders specific opcodes deterministically.</summary>
internal sealed class MemoryNetwork : IDatagramTransportFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<int, MemorySocket> _sockets = [];
    private int _nextPort = 40_000;
    private long _dataPackets;
    private long _ackPackets;
    private long _oackPackets;
    private long _errorPackets;
    private byte[]? _held;
    private IPEndPoint? _heldFrom;
    private IPEndPoint? _heldTo;

    public int DataDropsRemaining { get; set; }

    public int AckDropsRemaining { get; set; }

    public int OackDropsRemaining { get; set; }

    public bool DropAllData { get; set; }

    public int DuplicateDataRemaining { get; set; }

    public int DuplicateAckRemaining { get; set; }

    public int ReorderDataPairsRemaining { get; set; }

    public int CorruptDataRemaining { get; set; }

    public Action<IPEndPoint>? OnBound { get; set; }

    public long DataPackets
    {
        get { lock (_gate) return _dataPackets; }
    }

    public long AckPackets
    {
        get { lock (_gate) return _ackPackets; }
    }

    public long OackPackets
    {
        get { lock (_gate) return _oackPackets; }
    }

    public long ErrorPackets
    {
        get { lock (_gate) return _errorPackets; }
    }

    public IDatagramTransport Bind(IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        MemorySocket socket;
        lock (_gate)
        {
            var port = localEndPoint.Port == 0 ? _nextPort++ : localEndPoint.Port;
            if (_sockets.ContainsKey(port))
            {
                throw new InvalidOperationException($"Port {port} is already bound.");
            }

            socket = new MemorySocket(this, new IPEndPoint(localEndPoint.Address, port));
            _sockets.Add(port, socket);
        }

        OnBound?.Invoke(socket.LocalEndPoint);
        return socket;
    }

    internal void Unbind(int port)
    {
        lock (_gate)
        {
            _sockets.Remove(port);
        }
    }

    internal void Deliver(IPEndPoint from, IPEndPoint to, ReadOnlyMemory<byte> payload)
    {
        var copy = payload.ToArray();
        lock (_gate)
        {
            var opcode = copy.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(copy) : (ushort)0;
            switch (opcode)
            {
                case TftpOpcode.Data:
                    _dataPackets++;
                    break;
                case TftpOpcode.Acknowledgement:
                    _ackPackets++;
                    break;
                case TftpOpcode.OptionAcknowledgement:
                    _oackPackets++;
                    break;
                case TftpOpcode.Error:
                    _errorPackets++;
                    break;
            }

            if (ShouldDrop(opcode))
            {
                return;
            }

            if (opcode == TftpOpcode.Data && CorruptDataRemaining > 0)
            {
                CorruptDataRemaining--;
                copy = [0x00];
            }

            var duplicate = (opcode == TftpOpcode.Data && DuplicateDataRemaining > 0)
                || (opcode == TftpOpcode.Acknowledgement && DuplicateAckRemaining > 0);
            if (opcode == TftpOpcode.Data && DuplicateDataRemaining > 0)
            {
                DuplicateDataRemaining--;
            }

            if (opcode == TftpOpcode.Acknowledgement && DuplicateAckRemaining > 0)
            {
                DuplicateAckRemaining--;
            }

            if (opcode == TftpOpcode.Data && ReorderDataPairsRemaining > 0 && _held is null)
            {
                _held = copy;
                _heldFrom = from;
                _heldTo = to;
                return;
            }

            Enqueue(to, from, copy);
            if (duplicate)
            {
                Enqueue(to, from, copy.ToArray());
            }

            if (_held is not null && _heldTo is not null && _heldFrom is not null)
            {
                Enqueue(_heldTo, _heldFrom, _held);
                _held = null;
                _heldFrom = null;
                _heldTo = null;
                ReorderDataPairsRemaining--;
            }
        }
    }

    private bool ShouldDrop(ushort opcode)
    {
        if (opcode == TftpOpcode.Data && (DropAllData || DataDropsRemaining > 0))
        {
            if (DataDropsRemaining > 0)
            {
                DataDropsRemaining--;
            }

            return true;
        }

        if (opcode == TftpOpcode.Acknowledgement && AckDropsRemaining > 0)
        {
            AckDropsRemaining--;
            return true;
        }

        if (opcode == TftpOpcode.OptionAcknowledgement && OackDropsRemaining > 0)
        {
            OackDropsRemaining--;
            return true;
        }

        return false;
    }

    private void Enqueue(IPEndPoint to, IPEndPoint from, byte[] payload)
    {
        if (_sockets.TryGetValue(to.Port, out var socket))
        {
            socket.TryEnqueue(from, payload);
        }
    }
}

internal sealed class MemorySocket : IDatagramTransport
{
    private readonly MemoryNetwork _network;
    private readonly Channel<(IPEndPoint From, byte[] Payload)> _inbound = Channel.CreateBounded<(IPEndPoint, byte[])>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public MemorySocket(MemoryNetwork network, IPEndPoint localEndPoint)
    {
        _network = network;
        LocalEndPoint = localEndPoint;
    }

    public IPEndPoint LocalEndPoint { get; }

    public ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _network.Deliver(LocalEndPoint, remote, datagram);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<DatagramReceive> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var item = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        item.Payload.CopyTo(buffer.Span);
        return new DatagramReceive(item.Payload.Length, item.From);
    }

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _network.Unbind(LocalEndPoint.Port);
        return ValueTask.CompletedTask;
    }

    internal bool TryEnqueue(IPEndPoint from, byte[] payload) => _inbound.Writer.TryWrite((from, payload));
}
