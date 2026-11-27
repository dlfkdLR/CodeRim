//! Lowercase hexadecimal used by desktop identifiers and the mobile relay wire format.
pub(crate) fn write_lowercase_hex(bytes: &[u8], output: &mut [u8]) {
    const ALPHABET: &[u8; 16] = b"0123456789abcdef";
    for (&byte, pair) in bytes.iter().zip(output.as_chunks_mut::<2>().0) {
        pair[0] = ALPHABET[usize::from(byte >> 4)];
        pair[1] = ALPHABET[usize::from(byte & 15)];
    }
}

pub(crate) fn lowercase_sha256(bytes: &[u8; 32]) -> [u8; 64] {
    let mut ascii = [0; 64];
    write_lowercase_hex(bytes, &mut ascii);
    ascii
}
