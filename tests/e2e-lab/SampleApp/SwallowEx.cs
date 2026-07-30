using System;

namespace SampleApp
{
    public class SwallowEx
    {
        public void DoWork()
        {
            try
            {
                Risky();
            }
            catch { }
        }

        private void Risky()
        {
            throw new InvalidOperationException("boom");
        }
    }
}
