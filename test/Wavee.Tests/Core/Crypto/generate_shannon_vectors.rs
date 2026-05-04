// Shannon Cipher Test Vector Generator
//
// This Rust program generates authoritative test vectors for ShannonCipher
// by using the actual 'shannon' crate (v0.2.0) that librespot depends on.
//
// Usage:
//   cargo run --example generate_shannon_vectors
//
// Location in librespot:
//   librespot/core/examples/generate_shannon_vectors.rs

use byteorder::{BigEndian, ByteOrder};
use shannon::Shannon;

fn main() {
    println!("=== LIBRESPOT SHANNON CIPHER TEST VECTORS ===\n");
    println!("Generated using the shannon crate (same as librespot uses)\n");

    // Test keys - 32 bytes each (Shannon requires 32-byte keys)
    let send_key: [u8; 32] = [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f,
    ];

    let _recv_key: [u8; 32] = [
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d, 0x2e, 0x2f,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
        0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f,
    ];

    println!("Key: {}", hex_encode(&send_key));
    println!();

    // Test Case 1: Basic encryption with nonce 0
    println!("TEST CASE 1: Basic Encryption (Nonce 0)");
    println!("----------------------------------------");
    test_basic_encrypt(&send_key, 0, vec![0x01, 0x02, 0x03, 0x04]);

    // Test Case 2: Different nonce
    println!("\nTEST CASE 2: Different Nonce (Nonce 1)");
    println!("---------------------------------------");
    test_basic_encrypt(&send_key, 1, vec![0x01, 0x02, 0x03, 0x04]);

    // Test Case 3: Empty data
    println!("\nTEST CASE 3: Empty Data");
    println!("-----------------------");
    test_basic_encrypt(&send_key, 0, vec![]);

    // Test Case 4: Non-word-aligned (13 bytes = "Hello, World!")
    println!("\nTEST CASE 4: Non-word-aligned (13 bytes)");
    println!("-----------------------------------------");
    test_basic_encrypt(&send_key, 0, b"Hello, World!".to_vec());

    // Test Case 5: Packet format (like ApCodec)
    println!("\nTEST CASE 5: ApCodec-style Packet");
    println!("----------------------------------");
    test_packet_encrypt(&send_key, 0, 0x42, vec![0xAA, 0xBB, 0xCC, 0xDD]);

    // Test Case 6: Sequential nonces
    println!("\nTEST CASE 6: Sequential Nonces");
    println!("-------------------------------");
    test_sequential_nonces(&send_key);

    // Test Case 7: Large data
    println!("\nTEST CASE 7: Large Data (100 bytes)");
    println!("------------------------------------");
    let large_data: Vec<u8> = (0..100).map(|i| (i & 0xFF) as u8).collect();
    test_basic_encrypt(&send_key, 0, large_data);

    println!("\n=== FORMATTED TEST VECTORS FOR C# ===");
    println!("--------------------------------------\n");

    println!("// Key used for all tests");
    println!("byte[] key = new byte[] {{ {} }};", format_byte_array(&send_key));
    println!();

    println!("// Test Vector 1: 4-byte data, nonce 0");
    generate_csharp_vector(&send_key, 0, vec![0x01, 0x02, 0x03, 0x04]);

    println!("\n// Test Vector 2: Hello World, nonce 0");
    generate_csharp_vector(&send_key, 0, b"Hello, World!".to_vec());

    println!("\n// Test Vector 3: Packet format (cmd=0x42, payload=4 bytes), nonce 0");
    generate_csharp_packet_vector(&send_key, 0, 0x42, vec![0xAA, 0xBB, 0xCC, 0xDD]);

    println!("\n=== ALL TESTS COMPLETE ===");
}

fn test_basic_encrypt(key: &[u8; 32], nonce: u32, data: Vec<u8>) {
    let mut cipher = Shannon::new(key);
    cipher.nonce_u32(nonce);

    println!("Nonce:      {}", nonce);
    println!("Data Size:  {} bytes", data.len());

    if !data.is_empty() {
        if data.len() <= 32 {
            println!("Plaintext:  {}", hex_encode(&data));
        } else {
            println!("Plaintext:  {} (first 32 bytes)", hex_encode(&data[..32]));
        }
    } else {
        println!("Plaintext:  (empty)");
    }

    let mut encrypted = data.clone();
    cipher.encrypt(&mut encrypted);

    if !encrypted.is_empty() {
        if encrypted.len() <= 32 {
            println!("Encrypted:  {}", hex_encode(&encrypted));
        } else {
            println!("Encrypted:  {} (first 32 bytes)", hex_encode(&encrypted[..32]));
        }
    } else {
        println!("Encrypted:  (empty)");
    }

    let mut mac = [0u8; 4];
    cipher.finish(&mut mac);
    println!("MAC:        {}", hex_encode(&mac));
}

fn test_packet_encrypt(key: &[u8; 32], nonce: u32, cmd: u8, payload: Vec<u8>) {
    // Build packet: [cmd: 1 byte][length: 2 bytes big-endian][payload: N bytes]
    let mut packet = Vec::new();
    packet.push(cmd);
    packet.extend_from_slice(&(payload.len() as u16).to_be_bytes());
    packet.extend_from_slice(&payload);

    println!("Nonce:           {}", nonce);
    println!("Command:         0x{:02x}", cmd);
    println!("Payload Size:    {} bytes", payload.len());
    println!("Packet (plain):  {}", hex_encode(&packet));

    let mut cipher = Shannon::new(key);
    cipher.nonce_u32(nonce);

    let mut encrypted_packet = packet.clone();
    cipher.encrypt(&mut encrypted_packet);
    println!("Packet (enc):    {}", hex_encode(&encrypted_packet));

    let mut mac = [0u8; 4];
    cipher.finish(&mut mac);
    println!("MAC:             {}", hex_encode(&mac));
}

fn test_sequential_nonces(key: &[u8; 32]) {
    let data = vec![0x12, 0x34, 0x56, 0x78];

    for nonce in 0..3 {
        let mut cipher = Shannon::new(key);
        cipher.nonce_u32(nonce);

        let mut encrypted = data.clone();
        cipher.encrypt(&mut encrypted);

        let mut mac = [0u8; 4];
        cipher.finish(&mut mac);

        println!("Nonce {}: Encrypted={}, MAC={}", nonce, hex_encode(&encrypted), hex_encode(&mac));
    }
}

fn generate_csharp_vector(key: &[u8; 32], nonce: u32, data: Vec<u8>) {
    let mut cipher = Shannon::new(key);
    cipher.nonce_u32(nonce);

    let mut encrypted = data.clone();
    cipher.encrypt(&mut encrypted);

    let mut mac = [0u8; 4];
    cipher.finish(&mut mac);

    println!("uint nonce = {};", nonce);
    println!("byte[] plaintext = new byte[] {{ {} }};", format_byte_array(&data));
    println!("byte[] encrypted = new byte[] {{ {} }};", format_byte_array(&encrypted));
    println!("byte[] expectedMac = new byte[] {{ {} }};", format_byte_array(&mac));
}

fn generate_csharp_packet_vector(key: &[u8; 32], nonce: u32, cmd: u8, payload: Vec<u8>) {
    // Build packet
    let mut packet = Vec::new();
    packet.push(cmd);
    packet.extend_from_slice(&(payload.len() as u16).to_be_bytes());
    packet.extend_from_slice(&payload);

    let mut cipher = Shannon::new(key);
    cipher.nonce_u32(nonce);

    let mut encrypted = packet.clone();
    cipher.encrypt(&mut encrypted);

    let mut mac = [0u8; 4];
    cipher.finish(&mut mac);

    println!("uint nonce = {};", nonce);
    println!("byte cmd = 0x{:02x};", cmd);
    println!("byte[] payload = new byte[] {{ {} }};", format_byte_array(&payload));
    println!("byte[] packet = new byte[] {{ {} }};", format_byte_array(&packet));
    println!("byte[] encryptedPacket = new byte[] {{ {} }};", format_byte_array(&encrypted));
    println!("byte[] expectedMac = new byte[] {{ {} }};", format_byte_array(&mac));
}

fn hex_encode(data: &[u8]) -> String {
    data.iter().map(|b| format!("{:02x}", b)).collect::<String>()
}

fn format_byte_array(data: &[u8]) -> String {
    data.iter()
        .map(|b| format!("0x{:02x}", b))
        .collect::<Vec<_>>()
        .join(", ")
}
