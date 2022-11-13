using System;
using Common;

namespace HttpTransport.Transports
{
    public interface ISetHandler
    {
        ISetBuilder Handler(Action<Lifetime, IPipeline> pipeline);
    }
}
