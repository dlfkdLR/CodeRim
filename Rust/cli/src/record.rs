// Swift's keyed Codable containers require JSON objects and keep the first
// occurrence of a key. Serde's derived structs also accept positional arrays.
use crate::platform;
use serde::{
    Deserialize, Deserializer,
    de::{
        DeserializeSeed, IgnoredAny, IntoDeserializer, MapAccess, Visitor,
        value::MapAccessDeserializer,
    },
};
use std::{
    collections::{BTreeMap, HashSet},
    fmt,
    marker::PhantomData,
};

pub struct Record<T>(pub T);
impl<'de, T: Deserialize<'de>> Deserialize<'de> for Record<T> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        d.deserialize_map(ObjectVisitor(PhantomData))
    }
}
struct ObjectVisitor<T>(PhantomData<T>);
impl<'de, T: Deserialize<'de>> Visitor<'de> for ObjectVisitor<T> {
    type Value = Record<T>;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("a keyed JSON object")
    }
    fn visit_map<A: MapAccess<'de>>(self, map: A) -> Result<Self::Value, A::Error> {
        T::deserialize(MapAccessDeserializer::new(FirstKeys {
            map,
            seen: HashSet::new(),
        }))
        .map(Record)
    }
}
struct FirstKeys<A> {
    map: A,
    seen: HashSet<String>,
}
impl<'de, A: MapAccess<'de>> MapAccess<'de> for FirstKeys<A> {
    type Error = A::Error;
    fn next_key_seed<K: DeserializeSeed<'de>>(
        &mut self,
        seed: K,
    ) -> Result<Option<K::Value>, A::Error> {
        while let Some(key) = self.map.next_key::<String>()? {
            if self
                .seen
                .insert(platform::normalized_key(&key).into_owned())
            {
                return seed.deserialize(key.into_deserializer()).map(Some);
            }
            self.map.next_value::<IgnoredAny>()?;
        }
        Ok(None)
    }
    fn next_value_seed<V: DeserializeSeed<'de>>(&mut self, seed: V) -> Result<V::Value, A::Error> {
        self.map.next_value_seed(seed)
    }
}

pub fn required<'de, D: Deserializer<'de>, T: Deserialize<'de>>(d: D) -> Result<T, D::Error> {
    Record::deserialize(d).map(|r| r.0)
}
pub fn optional<'de, D: Deserializer<'de>, T: Deserialize<'de>>(
    d: D,
) -> Result<Option<T>, D::Error> {
    Option::<Record<T>>::deserialize(d).map(|v| v.map(|r| r.0))
}
pub fn list<'de, D: Deserializer<'de>, T: Deserialize<'de>>(d: D) -> Result<Vec<T>, D::Error> {
    Vec::<Record<T>>::deserialize(d).map(|v| v.into_iter().map(|r| r.0).collect())
}
pub fn dictionary<'de, D: Deserializer<'de>, T: Deserialize<'de>>(
    d: D,
) -> Result<BTreeMap<String, T>, D::Error> {
    Record::<BTreeMap<String, Record<T>>>::deserialize(d)
        .map(|v| v.0.into_iter().map(|(k, r)| (k, r.0)).collect())
}
