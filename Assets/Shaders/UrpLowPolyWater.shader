// Credit:
// This shader is a URP-native water shader.
// It was adapted from the original "Low Poly Water" shader which
// can be found at https://assetstore.unity.com/packages/tools/particles-effects/lowpoly-water-107563
Shader "Artifacts/URP Low Poly Water"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.372, 0.779, 0.816, 1)
        _FoamColor("Foam Color", Color) = (1, 1, 1, 1)
        [NoScaleOffset]_FoamTex("Foam Texture", 2D) = "black" {}
        _FoamTiling("Foam Tiling", Float) = 0.08
        _FoamScroll("Foam Scroll", Vector) = (0.06, 0.04, 0, 0)
        _FoamStrength("Foam Strength", Range(0, 2)) = 0.35
        _IntersectionFoamDistance("Intersection Foam Distance", Float) = 0.35
        _IntersectionFoamStrength("Intersection Foam Strength", Range(0, 2)) = 1.1
        _Smoothness("Smoothness", Range(0, 1)) = 0.85
        _SpecularStrength("Specular Strength", Range(0, 2)) = 0.65
        _FresnelPower("Fresnel Power", Range(0.1, 8)) = 3
        _RimStrength("Rim Strength", Range(0, 2)) = 0.3
        _Alpha("Alpha", Range(0, 1)) = 0.82
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float2 foamUV : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                half whitecapMask : TEXCOORD4;
            };

            TEXTURE2D(_FoamTex);
            SAMPLER(sampler_FoamTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _FoamColor;
                float _FoamTiling;
                float4 _FoamScroll;
                float _FoamStrength;
                float _IntersectionFoamDistance;
                float _IntersectionFoamStrength;
                float _Smoothness;
                float _SpecularStrength;
                float _FresnelPower;
                float _RimStrength;
                float _Alpha;
            CBUFFER_END

            half EvaluateIntersectionFoam(float4 positionCS, half foamMask)
            {
                float2 screenUV = GetNormalizedScreenSpaceUV(positionCS);
                float sceneRawDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = LinearEyeDepth(sceneRawDepth, _ZBufferParams);
                float waterEyeDepth = LinearEyeDepth(positionCS.z, _ZBufferParams);
                float depthDifference = max(sceneEyeDepth - waterEyeDepth, 0.0);

                // Foam is strongest where opaque geometry sits just behind the water surface.
                half intersectionMask = 1.0h - saturate(depthDifference / max(_IntersectionFoamDistance, 0.0001));
                half breakup = lerp(0.7h, 1.0h, foamMask);
                return intersectionMask * breakup * _IntersectionFoamStrength;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.foamUV = positionInputs.positionWS.xz * _FoamTiling + _Time.y * _FoamScroll.xy;
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                output.whitecapMask = saturate(input.color.a);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                Light mainLight = GetMainLight();

                half diffuse = saturate(dot(normalWS, mainLight.direction));
                half3 litColor = _BaseColor.rgb * (0.35h + 0.65h * diffuse);

                half3 halfVector = SafeNormalize(mainLight.direction + viewDirection);
                half specularPower = lerp(8.0h, 96.0h, _Smoothness);
                half specular = pow(saturate(dot(normalWS, halfVector)), specularPower) * _SpecularStrength;

                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirection)), _FresnelPower);
                half foamMask = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, input.foamUV).r;
                half surfaceFoam = saturate((foamMask - 0.45h) * 2.0h) * fresnel * _FoamStrength;
                half intersectionFoam = EvaluateIntersectionFoam(input.positionCS, foamMask);
                half whitecapFoam = input.whitecapMask * lerp(0.7h, 1.0h, foamMask);
                half foam = saturate(surfaceFoam + intersectionFoam + whitecapFoam);

                half3 color = litColor;
                color += mainLight.color * specular;
                color += _BaseColor.rgb * (fresnel * _RimStrength);
                color = lerp(color, _FoamColor.rgb, foam);
                color = MixFog(color, input.fogFactor);

                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }
}
