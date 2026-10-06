using System;
using System.Collections;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    // Observe the native coroutine's yields without changing their timing.
    internal static class FuelLandingDoorSequence
    {
        internal static IEnumerator Observe(IEnumerator native, Func<bool> doorsClosed, Action opened)
        {
            bool wasClosed=doorsClosed(), notified=false;
            try
            {
                while(native.MoveNext())
                {
                    bool closed=doorsClosed();
                    if(!notified && wasClosed && !closed){notified=true;opened();}
                    wasClosed=closed;
                    yield return native.Current;
                }
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
    }
}
