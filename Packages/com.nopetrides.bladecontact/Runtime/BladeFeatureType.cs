namespace BladeContact
{
    /// <summary>Semantic type authored into a <see cref="BladeShell"/> feature.</summary>
    public enum BladeFeatureType : byte
    {
        BroadFace,
        BevelFace,

        /// <summary>A line the consumer has DESIGNATED as a cutting edge.</summary>
        SharpEdge,

        /// <summary>
        /// A line in the profile that was not designated a cutting edge. It carries no claim of being a
        /// physically blunt edge.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Most such lines are simply where two facets of the discretised cross-section ring meet, and on
        /// a real blade they are not edges at all. Measured on SW-A1: the designated sharp edge
        /// <c>s49.e0</c> has a 25.00 degree dihedral, while <c>s49.e9</c> — previously labelled
        /// "BluntEdge" — sits on the blade CENTRELINE between two BroadFace facets at a 166.45 degree
        /// dihedral, i.e. 13.6 degrees from flat. Calling that a blunt edge asserted a physical property
        /// the geometry does not have, and it made an edge-versus-flat contact read as edge-versus-edge in
        /// traces.
        /// </para>
        /// <para>
        /// The name is therefore deliberately neutral: it says only "a line feature of the profile that is
        /// not a designated edge". Whether any given one is a real crease is answered by its dihedral, not
        /// by its label.
        /// </para>
        /// </remarks>
        ProfileFeatureEdge,

        Tip,
        Unresolved
    }
}
