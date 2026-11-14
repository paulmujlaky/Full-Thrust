// Mirrors MeshPush in Rendering/ModelLibrary.cs.
layout(push_constant) uniform Push {

    mat4 model;

    // Rows of last frame's model matrix, in last frame's render-local space.
    vec4 previous0;
    vec4 previous1;
    vec4 previous2;

    // x: shadow cascade the depth-only shaders project into.
    ivec4 view;

} pc;
