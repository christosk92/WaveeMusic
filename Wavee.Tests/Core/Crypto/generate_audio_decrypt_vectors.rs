// Audio Decryption Test Vector Generator
//
// This Rust program generates authoritative test vectors for AudioDecryptStream
// by using librespot's actual AudioDecrypt implementation.
//
// Usage:
//   cargo run --example generate_test_vectors
//
// Location in librespot:
//   librespot/audio/examples/generate_test_vectors.rs

use librespot_audio::AudioDecrypt;
use librespot_core::audio_key::AudioKey;
use std::io::{Cursor, Read, Seek, SeekFrom};

fn main() {
    println!("=== LIBRESPOT AUDIO DECRYPT TEST VECTORS ===\n");

    // Test Case 1: Basic encryption/decryption
    println!("TEST CASE 1: Basic Decryption");
    println!("------------------------------");

    let key_bytes: [u8; 16] = [
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
    ];
    let key = AudioKey(key_bytes);

    // Create some plaintext
    let plaintext: Vec<u8> = (0..100).map(|i| (i & 0xFF) as u8).collect();

    // First, encrypt the plaintext using the same logic
    let encrypted = encrypt_data(&plaintext, &key);

    println!("Key: {}", hex_encode(&key_bytes));
    println!("Plaintext (first 32 bytes): {}", hex_encode(&plaintext[..32]));
    println!("Encrypted (first 32 bytes): {}", hex_encode(&encrypted[..32]));

    // Now decrypt it back
    let cursor = Cursor::new(encrypted.clone());
    let mut decrypt = AudioDecrypt::new(Some(key), cursor);
    let mut decrypted = vec![0u8; plaintext.len()];
    decrypt.read_exact(&mut decrypted).unwrap();

    println!("Decrypted (first 32 bytes): {}", hex_encode(&decrypted[..32]));
    println!("Match: {}\n", plaintext == decrypted);

    // Test Case 2: Seeking to different positions
    println!("TEST CASE 2: Seeking Tests");
    println!("---------------------------");

    let test_positions = [0, 15, 16, 17, 31, 32, 64, 99];

    for &pos in &test_positions {
        let cursor = Cursor::new(encrypted.clone());
        let mut decrypt = AudioDecrypt::new(Some(key), cursor);

        decrypt.seek(SeekFrom::Start(pos as u64)).unwrap();
        let mut byte = [0u8; 1];
        decrypt.read_exact(&mut byte).unwrap();

        println!("Position {}: Encrypted=0x{:02x}, Decrypted=0x{:02x}, Expected=0x{:02x}, Match={}",
            pos, encrypted[pos], byte[0], plaintext[pos], byte[0] == plaintext[pos]);
    }

    // Test Case 3: Large data with multiple blocks
    println!("\nTEST CASE 3: Multi-block Data");
    println!("------------------------------");

    let large_plaintext: Vec<u8> = (0..256).map(|i| i as u8).collect();
    let large_encrypted = encrypt_data(&large_plaintext, &key);

    println!("Data size: {} bytes ({} AES blocks)", large_plaintext.len(), large_plaintext.len() / 16);
    println!("First block plaintext:  {}", hex_encode(&large_plaintext[0..16]));
    println!("First block encrypted:  {}", hex_encode(&large_encrypted[0..16]));
    println!("Second block plaintext: {}", hex_encode(&large_plaintext[16..32]));
    println!("Second block encrypted: {}", hex_encode(&large_encrypted[16..32]));
    println!("Last block plaintext:   {}", hex_encode(&large_plaintext[240..256]));
    println!("Last block encrypted:   {}", hex_encode(&large_encrypted[240..256]));

    // Test Case 4: Cross-block boundary reading
    println!("\nTEST CASE 4: Cross-Block Boundary");
    println!("-----------------------------------");

    let cursor = Cursor::new(large_encrypted.clone());
    let mut decrypt = AudioDecrypt::new(Some(key), cursor);

    // Seek to position 14 (2 bytes before block boundary)
    decrypt.seek(SeekFrom::Start(14)).unwrap();
    let mut boundary_data = vec![0u8; 4]; // Read across block boundary
    decrypt.read_exact(&mut boundary_data).unwrap();

    println!("Read 4 bytes starting at position 14 (crosses block boundary at 16):");
    println!("Encrypted: {}", hex_encode(&large_encrypted[14..18]));
    println!("Decrypted: {}", hex_encode(&boundary_data));
    println!("Expected:  {}", hex_encode(&large_plaintext[14..18]));
    println!("Match: {}", boundary_data == &large_plaintext[14..18]);

    // Test Case 5: Output formatted test vectors for C#
    println!("\n=== FORMATTED TEST VECTORS FOR C# ===");
    println!("--------------------------------------");

    let test_data: Vec<u8> = vec![
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
    ];

    let test_encrypted = encrypt_data(&test_data, &key);

    println!("// Test vector for C# validation");
    println!("byte[] key = new byte[] {{ {} }};",
        key_bytes.iter().map(|b| format!("0x{:02x}", b)).collect::<Vec<_>>().join(", "));
    println!("byte[] plaintext = new byte[] {{ {} }};",
        test_data.iter().map(|b| format!("0x{:02x}", b)).collect::<Vec<_>>().join(", "));
    println!("byte[] encrypted = new byte[] {{ {} }};",
        test_encrypted.iter().map(|b| format!("0x{:02x}", b)).collect::<Vec<_>>().join(", "));

    println!("\n=== ALL TESTS COMPLETE ===");
}

/// Encrypts data using the same AES-128-CTR algorithm as AudioDecrypt
fn encrypt_data(plaintext: &[u8], key: &AudioKey) -> Vec<u8> {
    let encrypted_cursor = Cursor::new(plaintext.to_vec());
    let mut decrypt = AudioDecrypt::new(Some(*key), encrypted_cursor);
    let mut result = vec![0u8; plaintext.len()];
    decrypt.read_exact(&mut result).unwrap();
    result
}

fn hex_encode(data: &[u8]) -> String {
    data.iter().map(|b| format!("{:02x}", b)).collect::<String>()
}
