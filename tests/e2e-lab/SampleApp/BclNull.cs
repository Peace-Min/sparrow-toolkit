using System;

namespace SampleApp
{
    public class BclNull
    {
        public object Create(string typeName)
        {
            var t = Type.GetType(typeName);
            return Activator.CreateInstance(t);
        }
    }
}
