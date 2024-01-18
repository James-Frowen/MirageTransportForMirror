# Mirage Transport For Mirror

[Mirage's](https://github.com/MirageNet/Mirage) Socket layer for [Mirror](https://github.com/MirrorNetworking/Mirror)


This transport is a RUDP implementation:
- Reliable channel
- Unreliable Channel
- Notify algorithm


### Notify algorithm 

The notify algorithm allows you to send unreliable message, and have the sender be told if the message was delivered or lost.

This is useful for low latency games where you only care a bout most recent data, but need to know what data the client has in order to send new data.

A good example of this is delta encoded movement data:
- client only cares about most recent position, and can ignore older packets
- Server can send difference between 2 packet, sending `x=500.1 -> x=500.2` as `x+=0.1` rather than `x=500.2`
- allows data to be better compressed to save bandwidth

### How to use Notify algorithm with Mirror?

Simply implement this interface and pass it to SendNotify Extension Method or the methods on the MirageTransport
```cs
public interface INotifyCallBack
{
    void OnDelivered();
    void OnLost();
}
```

```cs
public class Example
{
    private void SendSomeMessage(NetworkConnection conn, MyMessage msg)
    {
        var callback = new MyNotifyCallBack(conn);
        conn.SendNotify(msg, callback);
    }

    private struct MyMessage : NetworkMessage { }

    private class MyNotifyCallBack : INotifyCallBack
    {
        private NetworkConnection _conn;

        public MyNotifyCallBack(NetworkConnection conn)
        {
            _conn = conn;
        }

        public void OnDelivered()
        {
            Debug.Log($"Message was deliver to {_conn}");
        }

        public void OnLost()
        {
            Debug.Log($"Message was lost going to {_conn}");
        }
    }
}     
```
