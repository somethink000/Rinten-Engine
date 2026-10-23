/* SPDX-License-Identifier: MIT
 *
 * miniaudio and stb_vorbis, in the shape the managed audio code calls them.
 *
 * Everything that decides what plays - the output pump, the capture buffer,
 * sounds, samplers, pitch - is C# in Core/Backend/Audio. What is here is only
 * a way to reach two libraries whose structs are laid out by their headers and
 * change between versions: ma_device, ma_decoder and ma_linear_resampler are opened
 * here, held behind a pointer, and never seen from the other side. A managed
 * binding that spelled out their fields would be right for exactly one
 * release of miniaudio.
 *
 * Nothing here has an opinion but one - the resampler's filter is kept off
 * Nyquist, see rn_nyquist_factor. Every function is one call into the library
 * with the arguments translated, and the one piece of state - the struct that
 * carries the managed callback to the device thread - exists because a
 * ma_device wants a C function pointer and a user pointer, and that is what a
 * managed [UnmanagedCallersOnly] method plus a GCHandle is.
 */

#include "miniaudio.h"

#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"
#undef STB_VORBIS_HEADER_ONLY

#include <stdlib.h>
#include <string.h>

#if defined( _WIN32 )
	#define RN_EXPORT __declspec( dllexport )
#else
	#define RN_EXPORT __attribute__( ( visibility( "default" ) ) )
#endif

//What the managed side gives a device: called on the audio thread with the device's output buffer
//(playback) or input buffer (capture), whichever exists, and the frame count. Interleaved float32.
typedef void ( *rn_audio_data_fn )( void* user, float* output, const float* input, unsigned int frames );

typedef struct rn_audio_device
{
	ma_context context;
	ma_device device;
	rn_audio_data_fn callback;
	void* user;
} rn_audio_device;

static void rn_audio_forward( ma_device* device, void* output, const void* input, ma_uint32 frames )
{
	rn_audio_device* self = (rn_audio_device*)device->pUserData;
	if ( self == NULL || self->callback == NULL ) return;

	self->callback( self->user, (float*)output, (const float*)input, frames );
}

/* -- devices ---------------------------------------------------------------- */

//Opens a device without starting it. kind 0 = system, 1 = null backend; capture opens the mic
RN_EXPORT rn_audio_device* rn_audio_device_open( int kind, int capture, int sample_rate, int channels,
	int frames_per_buffer, rn_audio_data_fn callback, void* user,
	int* out_sample_rate, int* out_channels, char* out_name, int name_capacity )
{
	rn_audio_device* self = (rn_audio_device*)calloc( 1, sizeof( rn_audio_device ) );
	if ( self == NULL ) return NULL;

	self->callback = callback;
	self->user = user;

	ma_backend backends[] = { ma_backend_null };
	ma_context_config contextConfig = ma_context_config_init();

	ma_result contextResult = kind == 1
		? ma_context_init( backends, 1, &contextConfig, &self->context )
		: ma_context_init( NULL, 0, &contextConfig, &self->context );

	if ( contextResult != MA_SUCCESS )
	{
		free( self );
		return NULL;
	}

	ma_device_config config = ma_device_config_init( capture ? ma_device_type_capture : ma_device_type_playback );

	if ( capture )
	{
		config.capture.format = ma_format_f32;
		config.capture.channels = (ma_uint32)channels;
	}
	else
	{
		config.playback.format = ma_format_f32;
		config.playback.channels = (ma_uint32)channels;
	}

	config.sampleRate = (ma_uint32)sample_rate;
	if ( frames_per_buffer > 0 ) config.periodSizeInFrames = (ma_uint32)frames_per_buffer;
	config.dataCallback = rn_audio_forward;
	config.pUserData = self;

	if ( ma_device_init( &self->context, &config, &self->device ) != MA_SUCCESS )
	{
		ma_context_uninit( &self->context );
		free( self );
		return NULL;
	}

	if ( out_sample_rate != NULL ) *out_sample_rate = (int)self->device.sampleRate;
	if ( out_channels != NULL )
		*out_channels = (int)( capture ? self->device.capture.channels : self->device.playback.channels );

	if ( out_name != NULL && name_capacity > 0 )
	{
		const char* name = capture ? self->device.capture.name : self->device.playback.name;
		size_t length = strlen( name );
		if ( length >= (size_t)name_capacity ) length = (size_t)name_capacity - 1;
		memcpy( out_name, name, length );
		out_name[length] = '\0';
	}

	return self;
}

RN_EXPORT int rn_audio_device_start( rn_audio_device* self )
{
	if ( self == NULL ) return 0;
	return ma_device_start( &self->device ) == MA_SUCCESS ? 1 : 0;
}

/* Stops, and waits for the callback to be done, before anything it called
 * into can go away: uninit blocks until the audio thread has left. */
RN_EXPORT void rn_audio_device_close( rn_audio_device* self )
{
	if ( self == NULL ) return;

	ma_device_uninit( &self->device );
	ma_context_uninit( &self->context );
	free( self );
}

/* -- decoders --------------------------------------------------------------- */

//Over bytes the caller keeps alive for as long as the decoder lives: miniaudio reads from them as it goes and copies nothing. channels and
//sample_rate of zero mean the file's own. out_frames is the length, or zero when the format does not know it without being read through.
RN_EXPORT ma_decoder* rn_decoder_open( const void* data, size_t size, int channels, int sample_rate,
	int* out_channels, int* out_sample_rate, ma_uint64* out_frames )
{
	ma_decoder* decoder = (ma_decoder*)calloc( 1, sizeof( ma_decoder ) );
	if ( decoder == NULL ) return NULL;

	ma_decoder_config config = ma_decoder_config_init( ma_format_f32, (ma_uint32)channels, (ma_uint32)sample_rate );

	if ( ma_decoder_init_memory( data, size, &config, decoder ) != MA_SUCCESS )
	{
		free( decoder );
		return NULL;
	}

	if ( out_channels != NULL ) *out_channels = (int)decoder->outputChannels;
	if ( out_sample_rate != NULL ) *out_sample_rate = (int)decoder->outputSampleRate;

	if ( out_frames != NULL )
	{
		ma_uint64 frames = 0;
		*out_frames = ma_decoder_get_length_in_pcm_frames( decoder, &frames ) == MA_SUCCESS ? frames : 0;
	}

	return decoder;
}

/* Frames read, zero at the end or on an error - the two are the same thing to
 * a sampler, which then either loops or stops. */
RN_EXPORT ma_uint64 rn_decoder_read( ma_decoder* decoder, float* out, ma_uint64 frames )
{
	if ( decoder == NULL ) return 0;

	ma_uint64 read = 0;
	ma_result result = ma_decoder_read_pcm_frames( decoder, out, frames, &read );

	if ( result != MA_SUCCESS && read == 0 ) return 0;
	return read;
}

RN_EXPORT int rn_decoder_seek( ma_decoder* decoder, ma_uint64 frame )
{
	if ( decoder == NULL ) return 0;
	return ma_decoder_seek_to_pcm_frame( decoder, frame ) == MA_SUCCESS ? 1 : 0;
}

RN_EXPORT void rn_decoder_close( ma_decoder* decoder )
{
	if ( decoder == NULL ) return;

	ma_decoder_uninit( decoder );
	free( decoder );
}

/* -- resamplers ------------------------------------------------------------- */

/* The anti-alias filter's cutoff, as a share of what miniaudio would choose: half the lower rate, designed at the
 * higher. With the two rates nearly equal that is all but at Nyquist, where the order-4 filter's poles sit on the
 * unit circle and float rounding tips it over - a pitch of 1.0000006 at 44.1k is 44100 to 44099, and the voice, then
 * the whole mix, goes to silence and clicks and then full-scale noise. Kept 10% below Nyquist there; at rates 10%
 * or more apart this is 1 and the filter is miniaudio's own. */
static double rn_nyquist_factor( ma_uint32 in_rate, ma_uint32 out_rate )
{
	double lower = in_rate < out_rate ? in_rate : out_rate;
	double higher = in_rate < out_rate ? out_rate : in_rate;
	double factor = 0.9 * higher / lower;

	return factor < 1.0 ? factor : 1.0;
}

/* Linear, float32, interleaved. */
RN_EXPORT ma_linear_resampler* rn_resampler_open( int channels, int in_rate, int out_rate )
{
	if ( channels <= 0 || in_rate <= 0 || out_rate <= 0 ) return NULL;

	ma_linear_resampler* resampler = (ma_linear_resampler*)calloc( 1, sizeof( ma_linear_resampler ) );
	if ( resampler == NULL ) return NULL;

	ma_linear_resampler_config config = ma_linear_resampler_config_init( ma_format_f32, (ma_uint32)channels,
		(ma_uint32)in_rate, (ma_uint32)out_rate );

	/* Order 4, which ma_resampler gives its linear backend; its default is in miniaudio's implementation, not the header. */
	config.lpfOrder = 4;
	config.lpfNyquistFactor = rn_nyquist_factor( (ma_uint32)in_rate, (ma_uint32)out_rate );

	if ( ma_linear_resampler_init( &config, NULL, resampler ) != MA_SUCCESS )
	{
		free( resampler );
		return NULL;
	}

	return resampler;
}

/* The filter is redesigned for the new rates, its state kept, so a pitch can glide. */
RN_EXPORT int rn_resampler_set_rate( ma_linear_resampler* resampler, int in_rate, int out_rate )
{
	if ( resampler == NULL || in_rate <= 0 || out_rate <= 0 ) return 0;

	resampler->config.lpfNyquistFactor = rn_nyquist_factor( (ma_uint32)in_rate, (ma_uint32)out_rate );

	return ma_linear_resampler_set_rate( resampler, (ma_uint32)in_rate, (ma_uint32)out_rate ) == MA_SUCCESS ? 1 : 0;
}

RN_EXPORT void rn_resampler_reset( ma_linear_resampler* resampler )
{
	if ( resampler == NULL ) return;
	ma_linear_resampler_reset( resampler );
}

/* in_frames and out_frames are how many there are on the way in and how many
 * were used and made on the way out, as miniaudio counts them. */
RN_EXPORT int rn_resampler_process( ma_linear_resampler* resampler, const float* in, ma_uint64* in_frames,
	float* out, ma_uint64* out_frames )
{
	if ( resampler == NULL ) return 0;
	return ma_linear_resampler_process_pcm_frames( resampler, in, in_frames, out, out_frames ) == MA_SUCCESS ? 1 : 0;
}

RN_EXPORT void rn_resampler_close( ma_linear_resampler* resampler )
{
	if ( resampler == NULL ) return;

	ma_linear_resampler_uninit( resampler, NULL );
	free( resampler );
}

/* -- ogg -------------------------------------------------------------------- */

/* Decoded whole, as 16 bit interleaved samples that rn_vorbis_free gives back.
 * Frames decoded, or a number no greater than zero when it is not an ogg. */
RN_EXPORT int rn_vorbis_decode( const void* data, int size, int* out_channels, int* out_sample_rate, short** out_samples )
{
	return stb_vorbis_decode_memory( (const unsigned char*)data, size, out_channels, out_sample_rate, out_samples );
}

RN_EXPORT void rn_vorbis_free( short* samples )
{
	free( samples );
}
