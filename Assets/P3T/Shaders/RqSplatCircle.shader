// Billboarded circle splats, for drawing a Minkowski contact envelope as what it really is.
//
// WHY CIRCLES AND NOT GEOMETRY. The envelope at distance r from a surface is the union of spheres of
// radius r centred on that surface. A sphere's silhouette is ALWAYS a circle from every viewpoint, so a
// camera-facing filled circle is not an approximation of a sphere -- it is its exact projection. That
// makes this strictly more accurate than any triangulated sphere, at any zoom, and it is why the splat
// is drawn rather than tessellated: PhysX evaluates distance analytically and never tessellates either.
//
// One quad per splat. All four of a quad's vertices sit at the splat centre; the corner offset lives in
// the UV, and the billboarding happens in VIEW space so the circle faces the camera exactly without the
// mesh being rebuilt when the camera moves.
//
// URP 17.4. Unlit, transparent, no shadows, no lighting -- this is a diagnostic overlay, not a material.

Shader "P3T/RqSplatCircle"
{
    Properties
    {
        _Color ("Colour", Color) = (0.25, 0.85, 1.0, 0.28)
        _Radius ("Splat radius (m)", Float) = 0.01
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "SplatCircle"

            Blend SrcAlpha OneMinusSrcAlpha
            // ZTest on so the envelope is occluded by geometry in front of it, ZWrite off so overlapping
            // splats accumulate instead of one hiding the rest -- the union is the point.
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Radius;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 positionVS = TransformWorldToView(positionWS);

                // Billboard in view space: offsetting x and y there is exactly camera-facing, and needs no
                // per-frame mesh rebuild and no camera vectors passed in from script.
                positionVS.xy += IN.uv * _Radius;

                OUT.positionCS = TransformWViewToHClip(positionVS);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // The circle itself. Discarding outside the unit disc is what makes this a sphere's exact
                // silhouette rather than a quad.
                if (dot(IN.uv, IN.uv) > 1.0)
                    discard;

                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
