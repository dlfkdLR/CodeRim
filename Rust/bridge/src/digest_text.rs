//! Lowercase ASCII representation shared by desktop accounting and mobile ActivityKit IDs.
pub(crate) fn lowercase_sha256(bytes: &[u8; 32]) -> [u8; 64] {
    let alphabet = b"0123456789abcdef";
    let mut ascii = [0; 64];
    for (&byte, pair) in bytes.iter().zip(ascii.as_chunks_mut::<2>().0) {
        pair[0] = alphabet[usize::from(byte >> 4)];
        pair[1] = alphabet[usize::from(byte & 15)];
    }
    ascii
}
