Shader "Custom/PlanetWater"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.12, 0.42, 0.68, 0.88)
        _FresnelColor ("Fresnel Color", Color) = (0.8, 0.95, 1.0, 0.55)
        _Alpha ("Surface Alpha", Range(0, 1)) = 0.88
        _FresnelPower ("Fresnel Power", Range(0.25, 8)) = 2.2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _FresnelColor;
            float _Alpha;
            float _FresnelPower;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalize(normalInputs.normalWS);
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(positionInputs.positionWS);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                half3 viewDirWS = normalize(input.viewDirWS);
                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirWS)), _FresnelPower);

                half3 color = lerp(_BaseColor.rgb, _FresnelColor.rgb, fresnel * _FresnelColor.a);
                half alpha = saturate(_Alpha * _BaseColor.a);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
