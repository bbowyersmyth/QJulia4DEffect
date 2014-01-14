using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QJulia4DEffect.QJulia4D
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public float DiffuseR;
        public float DiffuseG;
        public float DiffuseB;
        public float DiffuseA;
        public float Mu1;
        public float Mu2;
        public float Mu3;
        public float Mu4;
        public float Epsilon;
        public int Width;
        public int Height;
        public int SelfShadow;
        public SharpDX.Matrix rotation;
        public SharpDX.Matrix lightRotation;
        public float zoom;
        public float rectX;
        public float rectY;
        public float rectWidth;
    }
    
    [PluginSupportInfo(typeof(PluginSupportInfo), DisplayName = "Julia 4D")]
    public class QJulia4DGPU : QJulia4DEffect.ComputeShaderBase
    {
        private static int BUFF_SIZE = Marshal.SizeOf(typeof(uint));

        private Tuple<double, double, double> rotation;
        private Tuple<double, double, double> lightRotation;
        private double zoom;
        private bool selfShadow;
        private double Mu1;
        private double Mu2;
        private double Mu3;
        private double Mu4;
        private SharpDX.Matrix viewMatrix;
        private SharpDX.Matrix lightMatrix;
        private bool isInitialized;
        private Device device;
        private ComputeShader shader;
        private ShaderBytecode shaderCode;
        private DeviceContext context;
        
        public enum PropertyNames
        {
            Rotation,
            LightRotation,
            Zoom,
            SelfShadow,
            Mu1,
            Mu2,
            Mu3,
            Mu4
        }

        public static string StaticName
        {
            get
            {
                return "Julia 4D";
            }
        }

        public static Bitmap StaticIcon
        {
            get
            {
                return new Bitmap(typeof(QJulia4DGPU), "QJulia4DIcon.png");
            }
        }

        public QJulia4DGPU()
            : base(QJulia4DGPU.StaticName, QJulia4DGPU.StaticIcon, SubmenuNames.Render, PaintDotNet.Effects.EffectFlags.Configurable | PaintDotNet.Effects.EffectFlags.SingleThreaded)
        {
            MaximumRegionWidth = 8024;
            MaximumRegionHeight = 8024;
            //CustomRegionHandling = true;
        }

        protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
        {
            ControlInfo configUI = PropertyBasedEffect.CreateDefaultConfigUI(props);

            configUI.SetPropertyControlValue(PropertyNames.Rotation, ControlInfoPropertyNames.DisplayName, "Rotation");
            configUI.SetPropertyControlType(PropertyNames.Rotation, PropertyControlType.RollBallAndSliders);
            configUI.SetPropertyControlValue(PropertyNames.LightRotation, ControlInfoPropertyNames.DisplayName, "Light");
            configUI.SetPropertyControlType(PropertyNames.LightRotation, PropertyControlType.RollBallAndSliders);
            configUI.SetPropertyControlValue(PropertyNames.Zoom, ControlInfoPropertyNames.DisplayName, "Zoom");
            configUI.SetPropertyControlValue(PropertyNames.Mu1, ControlInfoPropertyNames.DisplayName, "Dimension 1");
            configUI.SetPropertyControlValue(PropertyNames.Mu2, ControlInfoPropertyNames.DisplayName, "Dimension 2");
            configUI.SetPropertyControlValue(PropertyNames.Mu3, ControlInfoPropertyNames.DisplayName, "Dimension 3");
            configUI.SetPropertyControlValue(PropertyNames.Mu4, ControlInfoPropertyNames.DisplayName, "Dimension 4");
            configUI.SetPropertyControlValue(PropertyNames.SelfShadow, ControlInfoPropertyNames.DisplayName, "Self Shadow");
            configUI.SetPropertyControlValue(PropertyNames.SelfShadow, ControlInfoPropertyNames.Description, "Self Shadow");

            return configUI;
        }

        protected override PropertyCollection OnCreatePropertyCollection()
        {
            List<Property> props = new List<Property>();

            props.Add(new DoubleVector3Property(PropertyNames.Rotation, Tuple.Create<double, double, double>(0.0, 0.0, 0.0), Tuple.Create<double, double, double>(-180.0, -180.0, 0.0), Tuple.Create<double, double, double>(180.0, 180.0, 90.0)));
            props.Add(new DoubleVector3Property(PropertyNames.LightRotation, Tuple.Create<double, double, double>(0.0, 0.0, 0.0), Tuple.Create<double, double, double>(-180.0, -180.0, 0.0), Tuple.Create<double, double, double>(180.0, 180.0, 90.0)));
            props.Add(new DoubleProperty(PropertyNames.Mu1, 11828, 0, 32767));
            props.Add(new DoubleProperty(PropertyNames.Mu2, 8535, 0, 32767));
            props.Add(new DoubleProperty(PropertyNames.Mu3, 16383, 0, 32767));
            props.Add(new DoubleProperty(PropertyNames.Mu4, 16383, 0, 32767));
            props.Add(new DoubleProperty(PropertyNames.Zoom, 10, 0, 40));
            props.Add(new BooleanProperty(PropertyNames.SelfShadow, true));

            return new PropertyCollection(props);
        }

        protected override void OnDispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    CleanUp();
                }
                catch
                {
                }
            }

            base.OnDispose(disposing);
        }

        protected override void OnPreRender(RenderArgs dstArgs, RenderArgs srcArgs)
        {
            string shaderPath;

            CleanUp();
            
            try
            {
                shaderPath = "QJulia4DEffect.Shaders.QJulia4D.fx";

                // Create DirectX device and shaders
                CreateDevice(out device, out context, out shaderCode, out shader, shaderPath, out this.isInitialized);
            }
            catch (SharpDX.SharpDXException ex)
            {
                MessageBox.Show(ex.Message);
                this.isInitialized = false;
            }

            base.OnPreRender(dstArgs, srcArgs);
        }

        protected override void OnRenderRegion(Rectangle[] rois)
        {
            Surface dst = base.DstArgs.Surface;
            
            foreach (Rectangle rect in rois)
            {
                if (!this.isInitialized || base.IsCancelRequested)
                    return;
                
                // Compute Shader Parameters
                Constants consts = new Constants();
                consts.Width = dst.Width;
                consts.Height = dst.Height;
                consts.DiffuseA = 1.0f;
                consts.DiffuseB = 0.25f;
                consts.DiffuseG = 0.45f;
                consts.DiffuseR = 1.0f;
                consts.Epsilon = 0.003f;
                consts.Mu1 = (float)(2.0f * this.Mu1 / 32767f - 1.0f);
                consts.Mu2 = (float)(2.0f * this.Mu2 / 32767f - 1.0f);
                consts.Mu3 = (float)(2.0f * this.Mu3 / 32767f - 1.0f);
                consts.Mu4 = (float)(2.0f * this.Mu4 / 32767f - 1.0f);
                consts.rotation = viewMatrix;
                consts.lightRotation = lightMatrix;
                consts.SelfShadow = this.selfShadow ? 1 : 0;
                consts.zoom = (float)this.zoom / 10.0f;
                consts.rectX = rect.X;
                consts.rectY = rect.Y;
                consts.rectWidth = rect.Width;

                using (SharpDX.Direct3D11.Buffer constBuffer = CreateConstantBuffer(device, Marshal.SizeOf(consts)))
                using (SharpDX.Direct3D11.Buffer resultBuffer = CreateBuffer(device, rect.Width * rect.Height * BUFF_SIZE, BUFF_SIZE))
                {
                    using (UnorderedAccessView resultView = CreateUnorderedAccessView(device, resultBuffer))
                    {
                        unsafe
                        {
                            byte[] constsBytes = RawSerialize(consts);
                            fixed (byte* p = constsBytes)
                            {
                                var box = new SharpDX.DataBox((IntPtr)p);

                                context.UpdateSubresource(box, constBuffer);
                            }
                        }

                        RunComputerShader(context,
                            shader,
                            new ShaderResourceView[] { },
                            new UnorderedAccessView[] { resultView },
                            constBuffer,
                            (int)Math.Ceiling(rect.Width / 64.0),
                            rect.Height);
                        SharpDX.Direct3D11.Buffer copyBuf = CreateAndCopyBuffer(device, context, resultBuffer);
                        //SharpDX.DataStream ds;
                        //context.MapSubresource(copyBuf,
                        //    0,
                        //    MapMode.Read,
                        //    MapFlags.None,
                        //    out ds);

                        SharpDX.DataBox mappedResource = context.MapSubresource(copyBuf, 0, MapMode.Read, MapFlags.None);
                        
                        // Copy to destination pixels
                        //CopyStreamToSurface(ds, dst, rect);
                        CopyStreamToSurface(mappedResource, dst, rect);
                        context.UnmapSubresource(copyBuf, 0);

                        //ds.Close();
                        //ds.Dispose();
                        copyBuf.Dispose();
                    }
                }
            }
        }

        protected override void OnSetRenderInfo(PropertyBasedEffectConfigToken newToken, RenderArgs dstArgs, RenderArgs srcArgs)
        {
            this.rotation = newToken.GetProperty<DoubleVector3Property>(PropertyNames.Rotation).Value;
            this.lightRotation = newToken.GetProperty<DoubleVector3Property>(PropertyNames.LightRotation).Value;
            this.zoom = newToken.GetProperty<DoubleProperty>(PropertyNames.Zoom).Value;
            this.Mu1 = newToken.GetProperty<DoubleProperty>(PropertyNames.Mu1).Value;
            this.Mu2 = newToken.GetProperty<DoubleProperty>(PropertyNames.Mu2).Value;
            this.Mu3 = newToken.GetProperty<DoubleProperty>(PropertyNames.Mu3).Value;
            this.Mu4 = newToken.GetProperty<DoubleProperty>(PropertyNames.Mu4).Value;
            this.selfShadow = newToken.GetProperty<BooleanProperty>(PropertyNames.SelfShadow).Value;
            this.viewMatrix = CreateViewMatrix(
                this.rotation.Item1 * Math.PI / 180f,
                this.rotation.Item2 * Math.PI / 180f,
                this.rotation.Item3 * Math.PI / 180f);
            this.lightMatrix = CreateLightMatrix(
                this.viewMatrix,
                this.lightRotation.Item2 * Math.PI / 180f,
                this.lightRotation.Item3 * Math.PI / 180f);

            base.OnSetRenderInfo(newToken, dstArgs, srcArgs);
        }

        private void CleanUp()
        {
            shader.DisposeIfNotNull();
            shaderCode.DisposeIfNotNull();
            context.DisposeIfNotNull();
            device.DisposeIfNotNull();
        }
        
        private SharpDX.Matrix CreateViewMatrix(double angle, double tiltDirection, double tiltAmount)
        {
            double sinAngle = Math.Sin(angle);
            double cosAngle = Math.Cos(angle);
            SharpDX.Matrix spinMatrix = new SharpDX.Matrix();
            SharpDX.Matrix tiltMatrix;
                        
            spinMatrix.M11 = (float)cosAngle;
            spinMatrix.M12 = (float)-sinAngle;
            spinMatrix.M21 = (float)sinAngle;
            spinMatrix.M22 = (float)cosAngle;
            spinMatrix.M33 = (float)1;

            tiltMatrix = CreateTiltMatrix(tiltDirection, tiltAmount, false);

            return spinMatrix * tiltMatrix;
        }

        private SharpDX.Matrix CreateLightMatrix(SharpDX.Matrix viewMatrix, double tiltDirection, double tiltAmount)
        {
            return viewMatrix * CreateTiltMatrix(tiltDirection, tiltAmount, true);
        }

        private SharpDX.Matrix CreateTiltMatrix(double tiltDirection, double tiltAmount, bool reverse)
        {
            tiltDirection += (reverse ? 270.0 : 90.0) * Math.PI / 180f;

            SharpDX.Matrix tiltMatrix = new SharpDX.Matrix();
            double x = Math.Cos(tiltDirection);
            double y = Math.Sin(tiltDirection);
            double s = Math.Sin(tiltAmount);
            double c = Math.Cos(tiltAmount);
            double t = 1.0 - c;

            if (double.IsInfinity(x))
                x = 0;

            if (double.IsInfinity(y))
                y = 0;

            tiltMatrix.M11 = (float)(x * x * t + c);
            tiltMatrix.M12 = (float)(y * x * t);
            tiltMatrix.M13 = (float)(-y * s);
            tiltMatrix.M21 = (float)(x * y * t);
            tiltMatrix.M22 = (float)(y * y * t + c);
            tiltMatrix.M23 = (float)(x * s);
            tiltMatrix.M31 = (float)(y * s);
            tiltMatrix.M32 = (float)(-x * s);
            tiltMatrix.M33 = (float)(c);

            return tiltMatrix;
        }
    }
}