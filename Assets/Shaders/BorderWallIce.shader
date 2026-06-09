Shader "Artifacts/Border Wall Ice"
{
    Properties
    {
        _BottomColor("Bottom Color", Color) = (0.1, 0.23, 0.45, 1)
        _MidColor("Mid Color", Color) = (0.42, 0.68, 0.88, 1)
        _TopColor("Top Color", Color) = (0.97, 0.99, 1, 1)
        _AmbientFloor("Ambient Floor", Range(0, 1)) = 0.35
        _SnowBlendStart("Snow Blend Start", Range(0, 1)) = 0.68
        _Smoothness("Smoothness", Range(0, 1)) = 0.28
        _SpecularStrength("Specular Strength", Range(0, 2)) = 0.18
        _RimColor("Rim Color", Color) = (0.92, 0.97, 1, 1)
        _RimStrength("Rim Strength", Range(0, 1)) = 0.08
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
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half gradient : TEXCOORD2;
                half fogFactor : TEXCOORD3;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BottomColor;
                float4 _MidColor;
                float4 _TopColor;
                float _AmbientFloor;
                float _SnowBlendStart;
                float _Smoothness;
                float _SpecularStrength;
                float4 _RimColor;
                float _RimStrength;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.gradient = saturate(input.color.r);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half gradient = saturate(input.gradient);
                half midBlend = saturate(gradient * 1.2h);
                half snowBlend = smoothstep(_SnowBlendStart, 1.0h, gradient);

                half3 baseColor = lerp(_BottomColor.rgb, _MidColor.rgb, midBlend);
                baseColor = lerp(baseColor, _TopColor.rgb, snowBlend);

                half3 normalWS = normalize(input.normalWS);
                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                Light mainLight = GetMainLight();

                half diffuse = saturate(dot(normalWS, mainLight.direction));
                half wrappedDiffuse = _AmbientFloor + ((1.0h - _AmbientFloor) * diffuse);
                half3 bakedGi = SampleSH(normalWS);
                half3 lighting = max(bakedGi, _AmbientFloor.xxx) + (mainLight.color * wrappedDiffuse);

                half3 halfVector = SafeNormalize(mainLight.direction + viewDirection);
                half specularPower = lerp(10.0h, 72.0h, _Smoothness);
                half specular = pow(saturate(dot(normalWS, halfVector)), specularPower) * _SpecularStrength;
                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirection)), 3.0h);

                half3 color = baseColor * lighting;
                color += mainLight.color * specular;
                color = lerp(color, _RimColor.rgb, fresnel * _RimStrength);
                color = MixFog(color, input.fogFactor);

                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }
}
