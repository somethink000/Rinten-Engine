// SPDX-License-Identifier: MIT

//HDR pictures for the editor's texture compiler: tinyexr to read an .exr, basisu's BC6H encoder to pack one.
//Their implementations are basisu_tinyexr.cpp and basisu_transcoder.cpp, built on the desktops only

#include "../extern/basisu/encoder/3rdparty/tinyexr.h"
#include "../extern/basisu/transcoder/basisu_transcoder.h"
#include "../extern/basisu/transcoder/basisu_astc_hdr_core.h"

#include <stdlib.h>
#include <string.h>

#if defined( _WIN32 )
	#define RN_EXPORT __declspec( dllexport )
#else
	#define RN_EXPORT __attribute__( ( visibility( "default" ) ) )
#endif

//RGBA floats, rows from the top; null with the reason in `error`. Freed with rn_exr_free, not the caller's free
extern "C" RN_EXPORT float* rn_exr_load( const unsigned char* bytes, int byteCount, int* width, int* height,
	char* error, int errorSize )
{
	float* rgba = nullptr;
	const char* reason = nullptr;

	if ( error && errorSize > 0 ) error[0] = 0;

	if ( LoadEXRFromMemory( &rgba, width, height, bytes, (size_t)byteCount, &reason ) == TINYEXR_SUCCESS )
		return rgba;

	if ( reason )
	{
		if ( error && errorSize > 0 )
		{
			strncpy( error, reason, (size_t)errorSize - 1 );
			error[errorSize - 1] = 0;
		}

		FreeEXRErrorMessage( reason );
	}

	free( rgba );
	return nullptr;
}

//The Windows build has a static CRT of its own, so the pixels go back to the free that matches the malloc
extern "C" RN_EXPORT void rn_exr_free( float* rgba )
{
	free( rgba );
}

//One unsigned BC6H block from sixteen RGB halves in raster order, at the encoder's most thorough
extern "C" RN_EXPORT void rn_bc6h_encode( const unsigned short* rgb, unsigned char* block )
{
	//The encoder's tables; the first call builds them, under C++'s guarded static
	static const bool ready = ( basist::basisu_transcoder_init(), true );
	(void)ready;

	basist::astc_6x6_hdr::fast_bc6h_params params;
	params.m_num_diff_endpoint_modes_to_try = basist::astc_6x6_hdr::BC6H_NUM_DIFF_ENDPOINT_MODES_TO_TRY_9;
	params.m_max_2subset_pats_to_try = 32;
	params.m_hq_ls = true;
	params.m_brute_force_weight4_assignment = true;

	basist::astc_6x6_hdr::fast_encode_bc6h( rgb, reinterpret_cast<basist::bc6h_block*>( block ), params );
}
