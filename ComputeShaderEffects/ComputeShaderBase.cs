using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.PropertySystem;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace QJulia4DEffect
{
    public abstract class ComputeShaderBase : PaintDotNet.Effects.PropertyBasedEffect
    {
        [DllImport("kernel32.dll")]
        protected static extern void CopyMemory(IntPtr destination, IntPtr source, int length); 

        private static int COLOR_SIZE = Marshal.SizeOf(typeof(ColorBgra));
        private bool newRender = false;
        public int MaximumRegionWidth { get; set; }
        public int MaximumRegionHeight { get; set; }
        public int MaxTextureSize { get; private set; }
        public bool CustomRegionHandling { get; set; }

        protected ComputeShaderBase(string name, Image image, string subMenuName, PaintDotNet.Effects.EffectFlags flags)
            : base(name, image, subMenuName, flags)
        {
            MaxTextureSize = 8216;
            CustomRegionHandling = false;
        }

        internal static void CopyStreamToSurface(SharpDX.DataBox dbox, Surface dst, Rectangle rect)
        {
            IntPtr textureBuffer = dbox.DataPointer;
            IntPtr dstPointer = dst.GetPointPointer(rect.Left, rect.Top);

            if (rect.Width == dst.Width)
            {
                CopyMemory(dstPointer, textureBuffer, rect.Width * rect.Height * COLOR_SIZE);
            }
            else
            {
                int length = rect.Width * COLOR_SIZE;
                int dstStride = dst.Stride;
                int rectBottom = rect.Bottom;

                for (int y = rect.Top; y < rectBottom; y++)
                {

                    CopyMemory(dstPointer, textureBuffer, length);
                    textureBuffer += length;
                    dstPointer += dstStride;
                }
            }
        }
        
        internal unsafe static void CopyStreamToSurface(SharpDX.DataStream ds, Surface dst, Rectangle rect)
        {
            ColorBgra[] srcPixels = new ColorBgra[rect.Width];
            
            fixed (ColorBgra* pSrcPixels = srcPixels)
            {
                IntPtr pSrcPixels2 = (IntPtr)pSrcPixels;

                for (int y = rect.Top; y < rect.Bottom; y++)
                {
                    IntPtr pDstPixels = dst.GetPointPointer(rect.Left, y);

                    //IntPtr pDstPixels2 = (IntPtr)pDstPixels;
                    ds.ReadRange<ColorBgra>(srcPixels, 0, srcPixels.Length);

                    CopyMemory(pDstPixels, pSrcPixels2, rect.Width * COLOR_SIZE);
                }
            }
        }

        internal static SharpDX.Direct3D11.Buffer CreateAndCopyBuffer(Device device, DeviceContext context, SharpDX.Direct3D11.Buffer buffer)
        {
            BufferDescription desc = buffer.Description;

            desc.CpuAccessFlags = CpuAccessFlags.Read;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.OptionFlags = ResourceOptionFlags.None;
            SharpDX.Direct3D11.Buffer result = new SharpDX.Direct3D11.Buffer(device, desc);
            context.CopyResource(buffer, result);

            return result;
        }
        
        internal static SharpDX.Direct3D11.Buffer CreateBuffer(Device device, int sizeInBytes, int stride)
        {
            BufferDescription desc = new BufferDescription
            {
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                SizeInBytes = sizeInBytes,
                StructureByteStride = stride,
                OptionFlags = ResourceOptionFlags.BufferStructured
            };

            return new SharpDX.Direct3D11.Buffer(device, desc);
        }

        internal static SharpDX.Direct3D11.Buffer CreateConstantBuffer(Device device, int size)
        {
            BufferDescription desc = new BufferDescription
            {
                BindFlags = BindFlags.ConstantBuffer,
                SizeInBytes = size,
                Usage = ResourceUsage.Default,
                CpuAccessFlags = CpuAccessFlags.None
            };

            return new SharpDX.Direct3D11.Buffer(device, desc);
        }

        internal void CreateDevice(out Device device, out DeviceContext context, out ShaderBytecode shaderCode, out ComputeShader shader, string shaderPath, out bool isInitialized)
        {
            CreateDevice(out device, out context, out isInitialized);

            if (isInitialized)
            {
                CreateShader(device, out shaderCode, out shader, shaderPath);
            }
            else
            {
                shaderCode = null;
                shader = null;
            }
        }

        internal void CreateDevice(out Device device, out DeviceContext context, out bool isInitialized)
        {
            try
            {
                SharpDX.Direct3D.FeatureLevel[] level = new SharpDX.Direct3D.FeatureLevel[] { SharpDX.Direct3D.FeatureLevel.Level_11_0, SharpDX.Direct3D.FeatureLevel.Level_10_0 };
                device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.SingleThreaded, level);
                if (!device.CheckFeatureSupport(Feature.ComputeShaders))
                {
                    // GPU does not support compute shaders
                    device.Dispose();
                    device = new Device(SharpDX.Direct3D.DriverType.Warp, DeviceCreationFlags.SingleThreaded, level);

                    if (!device.CheckFeatureSupport(Feature.ComputeShaders))
                    {
                        // This version of Warp does not support compute shaders
                        device.Dispose();

                        isInitialized = false;
                        context = null;
                    }
                    else
                    {
                        isInitialized = true;
                        context = device.ImmediateContext;
                    }
                }
                else
                {
                    isInitialized = true;
                    context = device.ImmediateContext;
                }
            }
            catch
            {
                device = null;
                context = null;
                isInitialized = false;
            }

            if (!isInitialized)
            {
                System.Windows.Forms.MessageBox.Show("Device creation failed.\n\nPlease ensure that you have the latest drivers for your "
                    + "video card and that it supports DirectCompute.", "Hardware Accelerated Blur Pack");
            }
        }

        internal void CreateShader(Device device, out ShaderBytecode shaderCode, out ComputeShader shader, string shaderPath)
        {
            MemoryStream mem = new MemoryStream(GetEmbeddedContent(shaderPath));
            shaderCode = new ShaderBytecode(mem);
            shader = new ComputeShader(device, shaderCode);
        }
        
        internal static UnorderedAccessView CreateUnorderedAccessView(Device device, SharpDX.Direct3D11.Buffer buffer)
        {
            UnorderedAccessViewDescription desc = new UnorderedAccessViewDescription
            {
                Dimension = UnorderedAccessViewDimension.Buffer,
                Format = SharpDX.DXGI.Format.Unknown,
                Buffer = { FirstElement = 0, ElementCount = buffer.Description.SizeInBytes / buffer.Description.StructureByteStride }
            };

            return new UnorderedAccessView(device, buffer, desc);
        }
        
        internal static byte[] GetEmbeddedContent(string resourceName)
        {
            Stream resourceStream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            BinaryReader reader = new BinaryReader(resourceStream);
            return reader.ReadBytes((int)resourceStream.Length);
        }

        internal static Configuration GetDllConfig()
        {
            var configFile = System.Reflection.Assembly.GetExecutingAssembly().Location + ".config";
            var map = new ExeConfigurationFileMap
            {
                ExeConfigFilename = configFile
            };
            return ConfigurationManager.OpenMappedExeConfiguration(map, ConfigurationUserLevel.None);
        }

        internal static void RunComputerShader(DeviceContext context, ComputeShader shader, ShaderResourceView[] views, UnorderedAccessView[] unordered, SharpDX.Direct3D11.Buffer constParams, int x, int y)
        {
            ComputeShaderStage cs = context.ComputeShader;

            cs.Set(shader);
            //cs.SetShaderResources(views, 0, views.Length);
            cs.SetUnorderedAccessViews(0, unordered);
            cs.SetConstantBuffer(0, constParams);
            context.Dispatch(x, y, 1);
        }
        
        protected abstract override PropertyCollection OnCreatePropertyCollection();

        protected virtual void OnPreRender(RenderArgs dstArgs, RenderArgs srcArgs)
        {
        }

        protected override sealed void OnRender(Rectangle[] rois, int startIndex, int length)
        {
            if (length == 0)
                return;

            if (this.CustomRegionHandling && FullImageSelected(base.SrcArgs.Bounds))
            {
                if (this.newRender)
                {
                    this.newRender = false;
                    this.OnRenderRegion(SliceRectangles(new Rectangle[] { this.EnvironmentParameters.GetSelection(base.SrcArgs.Bounds).GetBoundsInt() }));
                }
            }
            else
            {
                this.OnRenderRegion(SliceRectangles(rois.Range<Rectangle>(startIndex, length).ToArray<Rectangle>()));
            }
        }

        protected virtual void OnRenderRegion(Rectangle[] rois)
        {
        }

        protected override void OnSetRenderInfo(PropertyBasedEffectConfigToken newToken, RenderArgs dstArgs, RenderArgs srcArgs)
        {
            this.newRender = true;
            base.OnSetRenderInfo(newToken, dstArgs, srcArgs);
            this.OnPreRender(dstArgs, srcArgs);
        }

        internal Rectangle[] SliceRectangles(Rectangle[] rois)
        {
            if (rois.Length == 0 || (this.MaximumRegionHeight == 0 && this.MaximumRegionWidth == 0))
                return rois;

            // Re-slice regions
            List<Rectangle> sizedRegions = new List<Rectangle>();
            Rectangle[] rectCopy = rois;

            // Resize width
            foreach (Rectangle rect in rectCopy)
            {
                if (this.MaximumRegionWidth > 0 && rect.Width > this.MaximumRegionWidth)
                {
                    int sliceCount = (int)Math.Ceiling((double)rect.Width / (double)this.MaximumRegionWidth);

                    for (int i = 0; i < sliceCount; i++)
                    {
                        if (i < sliceCount - 1)
                        {
                            sizedRegions.Add(new Rectangle(rect.X + (this.MaximumRegionWidth * i), rect.Y, this.MaximumRegionWidth, rect.Height));
                        }
                        else
                        {
                            int remainingWidth = rect.Width - this.MaximumRegionWidth * (sliceCount - 1);
                            sizedRegions.Add(new Rectangle(rect.Right - remainingWidth, rect.Y, remainingWidth, rect.Height));
                        }
                    }
                }
                else
                {
                    sizedRegions.Add(rect);
                }
            }

            rectCopy = sizedRegions.ToArray();
            sizedRegions.Clear();

            // Resize height
            foreach (Rectangle rect in rectCopy)
            {
                if (this.MaximumRegionHeight > 0 && rect.Height > this.MaximumRegionHeight)
                {
                    int sliceCount = (int)Math.Ceiling((double)rect.Height / (double)this.MaximumRegionHeight);

                    for (int i = 0; i < sliceCount; i++)
                    {
                        if (i < sliceCount - 1)
                        {
                            sizedRegions.Add(new Rectangle(rect.X, rect.Y + (this.MaximumRegionHeight * i), rect.Width, this.MaximumRegionHeight));
                        }
                        else
                        {
                            int remainingHeight = rect.Height - this.MaximumRegionHeight * (sliceCount - 1);
                            sizedRegions.Add(new Rectangle(rect.X, rect.Bottom - remainingHeight, rect.Width, remainingHeight));
                        }
                    }
                }
                else
                {
                    sizedRegions.Add(rect);
                }
            }

            return sizedRegions.ToArray();
        }
        
        internal bool FullImageSelected(Rectangle bounds)
        {
            Rectangle[] rois = this.EnvironmentParameters.GetSelection(bounds).GetRegionScansReadOnlyInt();
            return (rois.Length == 1 && rois[0] == bounds);
        }

        internal static byte[] RawSerialize(object value)
        {
            int rawsize = Marshal.SizeOf(value);
            byte[] rawdata = new byte[rawsize];

            GCHandle handle = GCHandle.Alloc(rawdata, GCHandleType.Pinned);
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            handle.Free();

            return rawdata;
        }
    }
}
