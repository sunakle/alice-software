using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AliceDesk
{
    internal abstract class BigSurWindow : Form
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected BigSurWindow()
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            DoubleBuffered = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            try {
                if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
            } catch (DllNotFoundException) { }
              catch (EntryPointNotFoundException) { }
        }
    }
}
