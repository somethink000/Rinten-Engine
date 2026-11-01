#ifndef ENGINE_CUT_COVERAGE
#define ENGINE_CUT_COVERAGE

//How much of a pixel a cut-out leaf keeps, as a sample mask: edge is pixels past the cut plus a half.
//The first sample cuts at a half, so one sample cuts where a discard would. Nought throws the pixel away
uint CutCoverage( float edge )
{
#ifdef PORTABLE
	return edge >= 0.5 ? 1u : 0u;
#else
	uint mask = 0u;
	if ( edge >= 0.5 ) mask |= 1u;
	if ( edge >= 0.25 ) mask |= 2u;
	if ( edge >= 0.75 ) mask |= 4u;
	if ( edge >= 0.125 ) mask |= 8u;
	if ( edge >= 0.625 ) mask |= 16u;
	if ( edge >= 0.375 ) mask |= 32u;
	if ( edge >= 0.875 ) mask |= 64u;
	if ( edge >= 0.0625 ) mask |= 128u;
	return mask;
#endif
}

#endif
