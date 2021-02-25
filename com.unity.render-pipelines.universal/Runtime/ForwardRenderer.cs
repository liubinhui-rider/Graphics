using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Default renderer for Universal RP.
    /// This renderer is supported on all Universal RP supported platforms.
    /// It uses a classic forward rendering strategy with per-object light culling.
    /// </summary>
    public sealed class ForwardRenderer : ScriptableRenderer
    {
        const int k_DepthStencilBufferBits = 32;
        const string k_CreateCameraTextures = "Create Camera Texture";

        ColorGradingLutPass m_ColorGradingLutPass;
        DepthOnlyPass m_DepthPrepass;
        MainLightShadowCasterPass m_MainLightShadowCasterPass;
        AdditionalLightsShadowCasterPass m_AdditionalLightsShadowCasterPass;
        ScreenSpaceShadowResolvePass m_ScreenSpaceShadowResolvePass;
        DrawObjectsPass m_RenderOpaqueForwardPass;
        DrawSkyboxPass m_DrawSkyboxPass;
        CopyDepthPass m_CopyDepthPass;
        CopyColorPass m_CopyColorPass;
        DrawObjectsPass m_RenderTransparentForwardPass;
        InvokeOnRenderObjectCallbackPass m_OnRenderObjectCallbackPass;
        PostProcessPass m_PostProcessPass;
        PostProcessPass m_FinalPostProcessPass;
        FinalBlitPass m_FinalBlitPass;
        CapturePass m_CapturePass;

#if UNITY_EDITOR
        SceneViewDepthCopyPass m_SceneViewDepthCopyPass;
#endif

        RenderTargetHandle m_ActiveCameraColorAttachment;
        RenderTargetHandle m_ActiveCameraDepthAttachment;
        RenderTargetHandle m_CameraColorAttachment;
        RenderTargetHandle m_CameraDepthAttachment;
        RenderTargetHandle m_DepthTexture;
        RenderTargetHandle m_OpaqueColor;
        RenderTargetHandle m_AfterPostProcessColor;
        RenderTargetHandle m_ColorGradingLut;

        ForwardLights m_ForwardLights;
        StencilState m_DefaultStencilState;

        /*
创建几个特殊材质，用的是配置里的shader，这几个材质会传给对应的pass。
设置模板测试StencilState结构体。
创建用到的每个pass，指定渲染RenderPassEvent。
设置各个rt
创建ForwardLights实例，用于光源的相关计算。
创建RenderingFeatures，这个类里只有cameraStacking一个bool值，像是个没开发完的功能。
         */
        public ForwardRenderer(ForwardRendererData data) : base(data)
        {
            // 根据4个shader创建材质  全屏拷贝 深度拷贝 采样 屏幕阴影
            Material blitMaterial = CoreUtils.CreateEngineMaterial(data.shaders.blitPS);
            Material copyDepthMaterial = CoreUtils.CreateEngineMaterial(data.shaders.copyDepthPS);
            Material samplingMaterial = CoreUtils.CreateEngineMaterial(data.shaders.samplingPS);
            Material screenspaceShadowsMaterial = CoreUtils.CreateEngineMaterial(data.shaders.screenSpaceShadowPS);
            // 获取forwarddata中的模板缓冲数据
            StencilStateData stencilData = data.defaultStencilState;
            m_DefaultStencilState = StencilState.defaultValue;
            m_DefaultStencilState.enabled = stencilData.overrideStencilState;
            m_DefaultStencilState.SetCompareFunction(stencilData.stencilCompareFunction);
            m_DefaultStencilState.SetPassOperation(stencilData.passOperation);
            m_DefaultStencilState.SetFailOperation(stencilData.failOperation);
            m_DefaultStencilState.SetZFailOperation(stencilData.zFailOperation);

            // Note: Since all custom render passes inject first and we have stable sort,
            // we inject the builtin passes in the before events.
            // 初始化各种pass 并且根据evt排序(实际上, evt是用于排序的, 排序在下方的代码有体现)
            m_MainLightShadowCasterPass = new MainLightShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);
            m_AdditionalLightsShadowCasterPass = new AdditionalLightsShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);
            m_DepthPrepass = new DepthOnlyPass(RenderPassEvent.BeforeRenderingPrepasses, RenderQueueRange.opaque, data.opaqueLayerMask);
            m_ScreenSpaceShadowResolvePass = new ScreenSpaceShadowResolvePass(RenderPassEvent.BeforeRenderingPrepasses, screenspaceShadowsMaterial);
            m_ColorGradingLutPass = new ColorGradingLutPass(RenderPassEvent.BeforeRenderingOpaques, data.postProcessData);
            m_RenderOpaqueForwardPass = new DrawObjectsPass("Render Opaques", true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            m_CopyDepthPass = new CopyDepthPass(RenderPassEvent.BeforeRenderingOpaques, copyDepthMaterial);
            m_DrawSkyboxPass = new DrawSkyboxPass(RenderPassEvent.BeforeRenderingSkybox);
            m_CopyColorPass = new CopyColorPass(RenderPassEvent.BeforeRenderingTransparents, samplingMaterial);
            m_RenderTransparentForwardPass = new DrawObjectsPass("Render Transparents", false, RenderPassEvent.BeforeRenderingTransparents, RenderQueueRange.transparent, data.transparentLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            m_OnRenderObjectCallbackPass = new InvokeOnRenderObjectCallbackPass(RenderPassEvent.BeforeRenderingPostProcessing);
            m_PostProcessPass = new PostProcessPass(RenderPassEvent.BeforeRenderingPostProcessing, data.postProcessData);
            m_FinalPostProcessPass = new PostProcessPass(RenderPassEvent.AfterRenderingPostProcessing, data.postProcessData);
            m_CapturePass = new CapturePass(RenderPassEvent.AfterRendering);
            m_FinalBlitPass = new FinalBlitPass(RenderPassEvent.AfterRendering, blitMaterial);

#if UNITY_EDITOR
            m_SceneViewDepthCopyPass = new SceneViewDepthCopyPass(RenderPassEvent.AfterRendering + 9, copyDepthMaterial);
#endif

            // RenderTexture format depends on camera and pipeline (HDR, non HDR, etc)
            // Samples (MSAA) depend on camera and pipeline
            m_CameraColorAttachment.Init("_CameraColorTexture");
            m_CameraDepthAttachment.Init("_CameraDepthAttachment");
            m_DepthTexture.Init("_CameraDepthTexture");
            m_OpaqueColor.Init("_CameraOpaqueTexture");
            m_AfterPostProcessColor.Init("_AfterPostProcessTexture");
            m_ColorGradingLut.Init("_InternalGradingLut");
            // 初始化灯光
            m_ForwardLights = new ForwardLights();
        }

        // 而SetUp函数做的就是把这些Pass组织起来,按什么顺序来定义这个渲染管线.下面截取一些来说明一下.
        // 其实总结来说,SetUp()就是决定了渲染管线中会有哪些步骤,这些步骤的渲染顺序是什么.而每一步里面该怎么渲染,该渲染什么,都是在这一步的pass中自己定义的.
        /// <inheritdoc />
        public override void Setup(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            ref CameraData cameraData = ref renderingData.cameraData;
            // 通过相机的msaa hdr等创建RT的描述
            RenderTextureDescriptor cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;

            // Special path for depth only offscreen cameras. Only write opaques + transparents. 
            // 如果是屏幕外的深度相机  只写入 opaques + transparents. 
            // 对于只渲染深度的相机，只需要添加3个pass，opaque、skybox、transparent。
            bool isOffscreenDepthTexture = camera.targetTexture != null && camera.targetTexture.format == RenderTextureFormat.Depth;
            if (isOffscreenDepthTexture)
            {
                ConfigureCameraTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);

                for (int i = 0; i < rendererFeatures.Count; ++i)
                    rendererFeatures[i].AddRenderPasses(this, ref renderingData);

                EnqueuePass(m_RenderOpaqueForwardPass);
                EnqueuePass(m_DrawSkyboxPass);
                EnqueuePass(m_RenderTransparentForwardPass);
                return;
            }

            // 设置阴影相关pass  是否支持阴影
            bool mainLightShadows = m_MainLightShadowCasterPass.Setup(ref renderingData);
            bool additionalLightShadows = m_AdditionalLightsShadowCasterPass.Setup(ref renderingData);
            bool resolveShadowsInScreenSpace = mainLightShadows && renderingData.shadowData.requiresScreenSpaceShadowResolve;

            // Depth prepass is generated in the following cases:
            // - We resolve shadows in screen space
            // - Scene view camera always requires a depth texture. We do a depth pre-pass to simplify it and it shouldn't matter much for editor.
            // - If game or offscreen camera requires it we check if we can copy the depth from the rendering opaques pass and use that instead.
            // 是否生成深度prepass
            bool requiresDepthPrepass = renderingData.cameraData.isSceneViewCamera ||
                (cameraData.requiresDepthTexture && (!CanCopyDepth(ref renderingData.cameraData)));
            requiresDepthPrepass |= resolveShadowsInScreenSpace;

            // TODO: There's an issue in multiview and depth copy pass. Atm forcing a depth prepass on XR until
            // we have a proper fix.
            if (cameraData.isStereoEnabled && cameraData.requiresDepthTexture)
                requiresDepthPrepass = true;

            bool createColorTexture = RequiresIntermediateColorTexture(ref renderingData, cameraTargetDescriptor)
                                      || rendererFeatures.Count != 0;

            // If camera requires depth and there's no depth pre-pass we create a depth texture that can be read
            // later by effect requiring it.
            // 没有深度prepass的情况下生成深度图
            bool createDepthTexture = cameraData.requiresDepthTexture && !requiresDepthPrepass;
            // 是否开启后处理
            bool postProcessEnabled = cameraData.postProcessEnabled;
            // 设置颜色和深度纹理的rendertargethandle
            m_ActiveCameraColorAttachment = (createColorTexture) ? m_CameraColorAttachment : RenderTargetHandle.CameraTarget;
            m_ActiveCameraDepthAttachment = (createDepthTexture) ? m_CameraDepthAttachment : RenderTargetHandle.CameraTarget;
            bool intermediateRenderTexture = createColorTexture || createDepthTexture;
            // 根据需求创建临时的RT
            if (intermediateRenderTexture)
                CreateCameraRenderTarget(context, ref cameraData);
            // 记录两个RT ID
            ConfigureCameraTarget(m_ActiveCameraColorAttachment.Identifier(), m_ActiveCameraDepthAttachment.Identifier());

            // if rendering to intermediate render texture we don't have to create msaa backbuffer
            // 如果有颜色和深度纹理 使用中间纹理就不再创建MSAA backbuffer
            int backbufferMsaaSamples = (intermediateRenderTexture) ? 1 : cameraTargetDescriptor.msaaSamples;
            
            if (Camera.main == camera && camera.cameraType == CameraType.Game && camera.targetTexture == null)
                SetupBackbufferFormat(backbufferMsaaSamples, renderingData.cameraData.isStereoEnabled);
            
            // 这些rendererFeatures是在RenderData的配置文件中设置的,相当于我们自己如果要在管线中做一些添加
            // 就自己写上renderPass,添加到这些feature里.Forward管线在这里把我们自己的pass加进来.
            // Feature是存储自定义pass数据的容器，可存储的数量不限，支持任何类型的数据。Feature一般用于扩展。
            for (int i = 0; i < rendererFeatures.Count; ++i)
            {
                rendererFeatures[i].AddRenderPasses(this, ref renderingData);
            }

            int count = activeRenderPassQueue.Count;
            for (int i = count - 1; i >= 0; i--)
            {
                if(activeRenderPassQueue[i] == null)
                    activeRenderPassQueue.RemoveAt(i);
            }
            // 查找是否有afterrender的pass
            bool hasAfterRendering = activeRenderPassQueue.Find(x => x.renderPassEvent == RenderPassEvent.AfterRendering) != null;

            // 在ForwardRenderer中,定义了一堆的ScriptableRenderPass.ScriptableRenderPass其实就相当于渲染管线中的一个个步骤.
            // 如m_DepthPrepass就相当于开始图片中Depth Texture这一步,m_RenderOpaqueForwardPass就相当于默认管线中的Opaque Object这一步.
            // 这里就是把一个个的步骤加进管线,在FrameDebug中可以看到一个个的步骤
            
            // 添加主光阴影pass
            if (mainLightShadows)
                EnqueuePass(m_MainLightShadowCasterPass);

            // 添加附加光阴影pass
            if (additionalLightShadows)
                EnqueuePass(m_AdditionalLightsShadowCasterPass);

            // 添加并设置深度预处理pass
            if (requiresDepthPrepass)
            {
                m_DepthPrepass.Setup(cameraTargetDescriptor, m_DepthTexture);
                EnqueuePass(m_DepthPrepass);
            }

            // 添加屏幕空间阴影pass
            if (resolveShadowsInScreenSpace)
            {
                m_ScreenSpaceShadowResolvePass.Setup(cameraTargetDescriptor);
                EnqueuePass(m_ScreenSpaceShadowResolvePass);
            }

            // 后处理是否开启，URP把后处理分成了两步，一个是实现常规特效的后处理，一个是抗锯齿这种，具体逻辑在PostProcessPass内部区分。
            if (postProcessEnabled)
            {
                m_ColorGradingLutPass.Setup(m_ColorGradingLut);
                EnqueuePass(m_ColorGradingLutPass);
            }
            // 不透明物体渲染pass
            EnqueuePass(m_RenderOpaqueForwardPass);
            // 天空盒pass
            if (camera.clearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null)
                EnqueuePass(m_DrawSkyboxPass);

            // If a depth texture was created we necessarily need to copy it, otherwise we could have render it to a renderbuffer
            // 拷贝深度贴图
            if (createDepthTexture)
            {
                m_CopyDepthPass.Setup(m_ActiveCameraDepthAttachment, m_DepthTexture);
                EnqueuePass(m_CopyDepthPass);
            }

            if (renderingData.cameraData.requiresOpaqueTexture)
            {
                // TODO: Downsampling method should be store in the renderer isntead of in the asset.
                // We need to migrate this data to renderer. For now, we query the method in the active asset.
                Downsampling downsamplingMethod = UniversalRenderPipeline.asset.opaqueDownsampling;
                m_CopyColorPass.Setup(m_ActiveCameraColorAttachment.Identifier(), m_OpaqueColor, downsamplingMethod);
                EnqueuePass(m_CopyColorPass);
            }

            // 添加半透明渲染Pass
            EnqueuePass(m_RenderTransparentForwardPass);
            EnqueuePass(m_OnRenderObjectCallbackPass);

            bool afterRenderExists = renderingData.cameraData.captureActions != null ||
                                     hasAfterRendering;

            bool requiresFinalPostProcessPass = postProcessEnabled &&
                                     renderingData.cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing;

            // if we have additional filters
            // we need to stay in a RT
            // 添加最后的几个pass 后处理pass 捕获pass 最终拷贝pass
            if (afterRenderExists)
            {
                bool willRenderFinalPass = (m_ActiveCameraColorAttachment != RenderTargetHandle.CameraTarget);
                // perform post with src / dest the same
                if (postProcessEnabled)
                {
                    m_PostProcessPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment, m_AfterPostProcessColor, m_ActiveCameraDepthAttachment, m_ColorGradingLut, requiresFinalPostProcessPass, !willRenderFinalPass);
                    EnqueuePass(m_PostProcessPass);
                }

                //now blit into the final target
                if (m_ActiveCameraColorAttachment != RenderTargetHandle.CameraTarget)
                {
                    if (renderingData.cameraData.captureActions != null)
                    {
                        m_CapturePass.Setup(m_ActiveCameraColorAttachment);
                        EnqueuePass(m_CapturePass);
                    }

                    if (requiresFinalPostProcessPass)
                    {
                        m_FinalPostProcessPass.SetupFinalPass(m_ActiveCameraColorAttachment);
                        EnqueuePass(m_FinalPostProcessPass);
                    }
                    else
                    {
                        m_FinalBlitPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment);
                        EnqueuePass(m_FinalBlitPass);
                    }
                }
            }
            else
            {
                if (postProcessEnabled)
                {
                    if (requiresFinalPostProcessPass)
                    {
                        m_PostProcessPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment, m_AfterPostProcessColor, m_ActiveCameraDepthAttachment, m_ColorGradingLut, true, false);
                        EnqueuePass(m_PostProcessPass);
                        m_FinalPostProcessPass.SetupFinalPass(m_AfterPostProcessColor);
                        EnqueuePass(m_FinalPostProcessPass);
                    }
                    else
                    {
                        m_PostProcessPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment, RenderTargetHandle.CameraTarget, m_ActiveCameraDepthAttachment, m_ColorGradingLut, false, true);
                        EnqueuePass(m_PostProcessPass);
                    }
                }
                else if (m_ActiveCameraColorAttachment != RenderTargetHandle.CameraTarget)
                {
                    m_FinalBlitPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment);
                    EnqueuePass(m_FinalBlitPass);
                }
            }

#if UNITY_EDITOR
            if (renderingData.cameraData.isSceneViewCamera)
            {
                m_SceneViewDepthCopyPass.Setup(m_DepthTexture);
                EnqueuePass(m_SceneViewDepthCopyPass);
            }
#endif
        }

        /// <inheritdoc />
        public override void SetupLights(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_ForwardLights.Setup(context, ref renderingData);
        }

        /// <inheritdoc />
        public override void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters,
            ref CameraData cameraData)
        {
            Camera camera = cameraData.camera;
            // TODO: PerObjectCulling also affect reflection probes. Enabling it for now.
            // if (asset.additionalLightsRenderingMode == LightRenderingMode.Disabled ||
            //     asset.maxAdditionalLightsCount == 0)
            // {
            //     cullingParameters.cullingOptions |= CullingOptions.DisablePerObjectCulling;
            // }

            // If shadow is disabled, disable shadow caster culling
            if (Mathf.Approximately(cameraData.maxShadowDistance, 0.0f))
                cullingParameters.cullingOptions &= ~CullingOptions.ShadowCasters;

            cullingParameters.shadowDistance = cameraData.maxShadowDistance;
        }

        /// <inheritdoc />
        public override void FinishRendering(CommandBuffer cmd)
        {
            if (m_ActiveCameraColorAttachment != RenderTargetHandle.CameraTarget)
                cmd.ReleaseTemporaryRT(m_ActiveCameraColorAttachment.id);

            if (m_ActiveCameraDepthAttachment != RenderTargetHandle.CameraTarget)
                cmd.ReleaseTemporaryRT(m_ActiveCameraDepthAttachment.id);
        }

        void CreateCameraRenderTarget(ScriptableRenderContext context, ref CameraData cameraData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(k_CreateCameraTextures);
            var descriptor = cameraData.cameraTargetDescriptor;
            int msaaSamples = descriptor.msaaSamples;
            if (m_ActiveCameraColorAttachment != RenderTargetHandle.CameraTarget)
            {
                bool useDepthRenderBuffer = m_ActiveCameraDepthAttachment == RenderTargetHandle.CameraTarget;
                var colorDescriptor = descriptor;
                colorDescriptor.depthBufferBits = (useDepthRenderBuffer) ? k_DepthStencilBufferBits : 0;
                cmd.GetTemporaryRT(m_ActiveCameraColorAttachment.id, colorDescriptor, FilterMode.Bilinear);
            }

            if (m_ActiveCameraDepthAttachment != RenderTargetHandle.CameraTarget)
            {
                var depthDescriptor = descriptor;
                depthDescriptor.colorFormat = RenderTextureFormat.Depth;
                depthDescriptor.depthBufferBits = k_DepthStencilBufferBits;
                depthDescriptor.bindMS = msaaSamples > 1 && !SystemInfo.supportsMultisampleAutoResolve && (SystemInfo.supportsMultisampledTextures != 0);
                cmd.GetTemporaryRT(m_ActiveCameraDepthAttachment.id, depthDescriptor, FilterMode.Point);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void SetupBackbufferFormat(int msaaSamples, bool stereo)
        {
#if ENABLE_VR && ENABLE_VR_MODULE
            bool msaaSampleCountHasChanged = false;
            int currentQualitySettingsSampleCount = QualitySettings.antiAliasing;
            if (currentQualitySettingsSampleCount != msaaSamples &&
                !(currentQualitySettingsSampleCount == 0 && msaaSamples == 1))
            {
                msaaSampleCountHasChanged = true;
            }

            // There's no exposed API to control how a backbuffer is created with MSAA
            // By settings antiAliasing we match what the amount of samples in camera data with backbuffer
            // We only do this for the main camera and this only takes effect in the beginning of next frame.
            // This settings should not be changed on a frame basis so that's fine.
            QualitySettings.antiAliasing = msaaSamples;

            if (stereo && msaaSampleCountHasChanged)
                XR.XRDevice.UpdateEyeTextureMSAASetting();
#else
            QualitySettings.antiAliasing = msaaSamples;
#endif
        }
        
        bool RequiresIntermediateColorTexture(ref RenderingData renderingData, RenderTextureDescriptor baseDescriptor)
        {
            ref CameraData cameraData = ref renderingData.cameraData;
            int msaaSamples = cameraData.cameraTargetDescriptor.msaaSamples;
            bool isStereoEnabled = renderingData.cameraData.isStereoEnabled;
            bool isScaledRender = !Mathf.Approximately(cameraData.renderScale, 1.0f);
            bool isCompatibleBackbufferTextureDimension = baseDescriptor.dimension == TextureDimension.Tex2D;
            bool requiresExplicitMsaaResolve = msaaSamples > 1 && !SystemInfo.supportsMultisampleAutoResolve;
            bool isOffscreenRender = cameraData.camera.targetTexture != null && !cameraData.isSceneViewCamera;
            bool isCapturing = cameraData.captureActions != null;

#if ENABLE_VR && ENABLE_VR_MODULE
            if (isStereoEnabled)
                isCompatibleBackbufferTextureDimension = UnityEngine.XR.XRSettings.deviceEyeTextureDimension == baseDescriptor.dimension;
#endif

            bool requiresBlitForOffscreenCamera = cameraData.postProcessEnabled || cameraData.requiresOpaqueTexture || requiresExplicitMsaaResolve;
            if (isOffscreenRender)
                return requiresBlitForOffscreenCamera;

            return requiresBlitForOffscreenCamera || cameraData.isSceneViewCamera || isScaledRender || cameraData.isHdrEnabled ||
                   !isCompatibleBackbufferTextureDimension || !cameraData.isDefaultViewport || isCapturing || Display.main.requiresBlitToBackbuffer
                   || (renderingData.killAlphaInFinalBlit && !isStereoEnabled);
        }

        bool CanCopyDepth(ref CameraData cameraData)
        {
            bool msaaEnabledForCamera = cameraData.cameraTargetDescriptor.msaaSamples > 1;
            bool supportsTextureCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None;
            bool supportsDepthTarget = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.Depth);
            bool supportsDepthCopy = !msaaEnabledForCamera && (supportsDepthTarget || supportsTextureCopy);

            // TODO:  We don't have support to highp Texture2DMS currently and this breaks depth precision.
            // currently disabling it until shader changes kick in.
            //bool msaaDepthResolve = msaaEnabledForCamera && SystemInfo.supportsMultisampledTextures != 0;
            bool msaaDepthResolve = false;
            return supportsDepthCopy || msaaDepthResolve;
        }
    }
}
