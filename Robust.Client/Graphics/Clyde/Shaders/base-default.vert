// Vertex position.
/*layout (location = 0)*/ attribute vec2 aPos;
// Texture coordinates.
/*layout (location = 1)*/ attribute vec2 tCoord;
/*layout (location = 2)*/ attribute vec2 tCoord2;
// Colour modulation.
/*layout (location = 3)*/ attribute vec4 modulate;

varying vec2 UV;
varying vec2 UV2;
varying vec2 Pos;
varying vec4 VtxModulate;

uniform mat3 modelMatrix;

uniform vec4 modifyUV;

// WH14 grass decor wind
uniform float wh14DecorWindEnabled;
uniform float wh14WindTime;
uniform float wh14WindPower;
uniform float wh14WindDirection;
uniform float wh14WindGust;

// [SHADER_HEADER_CODE]

void main()
{
    vec2 wh14Pos = aPos;

    if (wh14DecorWindEnabled > 0.5)
    {
        float localY = fract(tCoord.y * 3.0);

        float windWeight = smoothstep(0.08, 0.95, localY);
        windWeight = pow(windWeight, 1.35);

        float wave =
        sin(
            wh14WindTime * 2.4
            + aPos.x * 0.55
            + aPos.y * 0.38);

        float gust =
        1.0
        + sin(
            wh14WindTime * 0.85
            + aPos.x * 0.12
            + aPos.y * 0.09)
        * wh14WindGust;

        vec2 windDir = vec2(cos(wh14WindDirection), sin(wh14WindDirection));

        float strength = mix(0.0, 0.55, wh14WindPower);

        wh14Pos += windDir * wave * gust * strength * windWeight;
    }

    vec3 transformed = projectionMatrix * viewMatrix * modelMatrix * vec3(wh14Pos, 1.0);
    vec2 VERTEX = transformed.xy;

    // [SHADER_CODE]

    VERTEX += 1.0;
    VERTEX /= SCREEN_PIXEL_SIZE * 2.0;
    VERTEX = floor(VERTEX + 0.5);
    VERTEX *= SCREEN_PIXEL_SIZE * 2.0;
    VERTEX -= 1.0;

    gl_Position = vec4(VERTEX, 0.0, 1.0);
    Pos = (VERTEX + 1.0) / 2.0;
    UV = mix(modifyUV.xy, modifyUV.zw, tCoord);
    UV2 = tCoord2;

    if (modulate.x < 0.0)
    {
        VtxModulate = -1.0 - zFromSrgb(-1.0 - modulate);
    }
    else
    {
        VtxModulate = zFromSrgb(modulate);
    }
}
